//
// Copyright (c) 2010-2024 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Linq;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.CPU.Disassembler;
using Google.FlatBuffers;
using FBInstruction; // <auto-generated with flatc -csharp>
using Antmicro.Renode.Logging.Profiling;

namespace Antmicro.Renode.Peripherals.CPU
{
    public class TraceBasedModelFlatBufferWriter : TraceWriter
    {
        public TraceBasedModelFlatBufferWriter(TranslationCPU cpu, string path, TraceFormat format, bool compress)
            : base(cpu, path, format, compress)
        {
            disassemblyCache = new LRUCache<uint, Disassembler.DisassemblyResult>(CacheSize);
            instructionsBuffer = new List<InstructionTrace>();
            vectorConfig = new Tuple<float, byte, short>(0, 0, -1);
        }

        public override void Write(ExecutionTracer.Block block)
        {
            var pc = block.FirstInstructionPC;
            var pcVirtual = block.FirstInstructionVirtualPC;
            var counter = 0;
            var hasAdditionalData = block.AdditionalDataInTheBlock.TryDequeue(out var nextAdditionalData);

            while(counter < (int)block.InstructionsCount)
            {
                if(!TryReadAndDisassembleInstruction(pc, block.DisassemblyFlags, out var result))
                {
                    break;
                }

                var additionalData = new List<AdditionalData>();
                while(hasAdditionalData && (nextAdditionalData.PC == pcVirtual))
                {
                    additionalData.Add(nextAdditionalData);
                    hasAdditionalData = block.AdditionalDataInTheBlock.TryDequeue(out nextAdditionalData);
                }

                var opcode = Misc.HexStringToByteArray(result.OpcodeString.Trim(), true);

                instructionsBuffer.Add(new InstructionTrace(result, opcode, additionalData));

                pc += (ulong)result.OpcodeSize;
                pcVirtual += (ulong)result.OpcodeSize;
                counter++;
            }
            FlushBuffer();
        }

        public override void FlushBuffer()
        {
            var builder = new FlatBufferBuilder(InitialFlatBufferSize);
            var instructions = instructionsBuffer.Select(x => BuildInstructionFlatbuffer(builder, x.Result, x.Opcode, x.AdditionalData)).ToArray();
            var instrsVector = Instructions.CreateInstructionsVector(builder, instructions);
            Instructions.StartInstructions(builder);
            Instructions.AddInstructions(builder, instrsVector);
            var instrs = Instructions.EndInstructions(builder);
            Instructions.FinishInstructionsBuffer(builder, instrs);

            var buf = builder.DataBuffer.ToSizedArray();
            // Write the size of the buffer
            stream.Write(BitConverter.GetBytes(buf.Length), 0, sizeof(int));
            stream.Write(buf, 0, buf.Length);
            stream.Flush();

            builder.Clear();
            instructionsBuffer.Clear();
        }

        protected override void Dispose(bool disposing)
        {
            if(disposed)
            {
                return;
            }

            if(disposing)
            {
                FlushBuffer();
                stream?.Dispose();
            }
            disposed = true;
        }

        private Offset<Instruction> BuildInstructionFlatbuffer(FlatBufferBuilder builder, DisassemblyResult result, byte[] opcode, List<AdditionalData> additionalData)
        {
            if(result.DisassemblyString == null)
            {
                // return a default instruction if the disassembly failed
                Logger.Log(LogLevel.Warning, $"Disassembly is not available for the instruction at 0x{result.PC:X}. Returning default TBM instruction.");
                return Instruction.CreateInstruction(builder);
            }
            
            var disasString = result.DisassemblyString.Trim();
            var sepIdx = disasString.IndexOf('\t');

            var mnemonic = "";
            var operands = new string[0];

            if(sepIdx == -1)
            {
                mnemonic = disasString;
            }
            else
            {
                mnemonic = disasString.Substring(0, sepIdx);
                var operandsString = disasString.Substring(sepIdx + 1);
                operands = operandsString.Split(new string[] { ", " }, StringSplitOptions.None);
            }

            var mnemonicOffset = builder.CreateString(mnemonic);

            var operandStringOffsetVector = operands.Select(x => builder.CreateString(x)).ToArray();
            var operandsVector = Instruction.CreateOperandsVector(builder, operandStringOffsetVector);

            var inputsAndOutputs = TBMRiscVHelper.AsmRegisters(mnemonic, operands);
            var inputsStringOffsetVector = inputsAndOutputs.Item1.Select(x => builder.CreateString(x)).ToArray();
            var outputsStringOffsetVector = inputsAndOutputs.Item2.Select(x => builder.CreateString(x)).ToArray();
            var inputsVector = Instruction.CreateInputsVector(builder, inputsStringOffsetVector);
            var outputsVector = Instruction.CreateOutputsVector(builder, outputsStringOffsetVector);

            var stores = new List<ulong>();
            var loads = new List<ulong>();

            // Execution tracer accesses additional data through hooks mounted at a given PC.
            // Available types of data include memory access and vector configuration.
            foreach(var additionalDataEntry in additionalData)
            {
                switch(additionalDataEntry.Type)
                {
                    case AdditionalDataType.MemoryAccess:
                        var type = (additionalDataEntry as MemoryAccessAdditionalData).OperationType;
                        var address = (additionalDataEntry as MemoryAccessAdditionalData).OperationTarget;
                        switch(type)
                        {
                            case MemoryOperation.MemoryWrite:
                            case MemoryOperation.MemoryIOWrite:
                                stores.Add(address);
                                break;
                            case MemoryOperation.MemoryRead:
                            case MemoryOperation.MemoryIORead:
                                loads.Add(address);
                                break;
                            case MemoryOperation.InsnFetch: // the instruction fetch is not included according to the documentation
                            default:
                                break;
                        }
                        break;
                    case AdditionalDataType.RiscVVectorConfiguration:
                        // Single instruction should have maximum one entry of vector configuration type
                        // so it shouldn't overwrite the previous value.

                        var vtype = (additionalDataEntry as RiscVVectorConfigurationData).VectorType;
                        // vector length multiplier
                        var vlmul = BitHelper.GetValue(vtype, 0, 3);
                        var lmul = TBMRiscVHelper.GetVectorLengthMultiplier(vlmul);
                        // element width
                        var vsew = BitHelper.GetValue(vtype, 3, 3);
                        var sew = TBMRiscVHelper.GetSelectedElementWidth(vsew);
                        // vector length
                        var vl = (short)(additionalDataEntry as RiscVVectorConfigurationData).VectorLength;
                        // Keep the current vector configuration to pass it to other vector instructions.
                        vectorConfig = new Tuple<float, byte, short>(lmul, sew, vl);
                        break;
                    case AdditionalDataType.None:
                    default:
                        break;
                }
            }

            var isVectorInstruction = TBMRiscVHelper.IsVectorInstruction(inputsAndOutputs.Item1, inputsAndOutputs.Item2);

            var storesVector = Instruction.CreateStoresVector(builder, stores.ToArray());
            var loadsVector = Instruction.CreateLoadsVector(builder, loads.ToArray());

            Instruction.StartInstruction(builder);
            Instruction.AddAddr(builder, result.PC);
            Instruction.AddOpcode(builder, BitHelper.ToUInt32(opcode, 0, opcode.Length, false));
            Instruction.AddMnemonic(builder, mnemonicOffset);
            Instruction.AddOperands(builder, operandsVector);
            Instruction.AddInputs(builder, inputsVector);
            Instruction.AddOutputs(builder, outputsVector);
            Instruction.AddIsNop(builder, TBMRiscVHelper.IsNop(mnemonic));
            Instruction.AddIsBranch(builder, TBMRiscVHelper.IsBranch(mnemonic));
            Instruction.AddBranchTarget(builder, 0);
            Instruction.AddIsFlush(builder, TBMRiscVHelper.IsFlush(mnemonic));
            Instruction.AddIsVctrl(builder, TBMRiscVHelper.IsVctrl(mnemonic));
            Instruction.AddLoads(builder, loadsVector);
            Instruction.AddStores(builder, storesVector);
            Instruction.AddLmul(builder, isVectorInstruction ? vectorConfig.Item1 : 0);
            Instruction.AddSew(builder, isVectorInstruction ? vectorConfig.Item2 : (byte)0);
            Instruction.AddVl(builder, isVectorInstruction ? vectorConfig.Item3 : (short)-1);

            return Instruction.EndInstruction(builder);
        }

        private bool disposed;
        private readonly List<InstructionTrace> instructionsBuffer;
        private Tuple<float, byte, short> vectorConfig;
        
        private const int InitialFlatBufferSize = 1024;
        private const int CacheSize = 100000;

        private class InstructionTrace
        {
            public InstructionTrace(DisassemblyResult result, byte[] opcode, List<AdditionalData> additionalData)
            {
                Result = result;
                Opcode = opcode;
                AdditionalData = additionalData;
            }

            public DisassemblyResult Result { get; }
            public byte[] Opcode { get; }
            public List<AdditionalData> AdditionalData { get; }
        }
    }
}
