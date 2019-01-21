﻿using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Iced.Intel;
using Microsoft.Diagnostics.Runtime;
using Decoder = Iced.Intel.Decoder;

namespace JitBuddy
{
    /// <summary>
    /// Provides an extension method to dump the ASM generated by the current JIT of a <see cref="MethodInfo"/>.
    /// </summary>
    public static class JitBuddy
    {
        private static readonly object initLock = new object();
        private static ClrRuntime _runtime = null;

        /// <summary>
        /// Disassemble the method.
        /// </summary>
        /// <param name="method">A method to disassemble</param>
        /// <returns>A string representation of the ASM of the method being jitted</returns>
        public static string ToAsm(this MethodInfo method)
        {
            var builder = new StringBuilder();
            ToAsm(method, builder);
            return builder.ToString();
        }

        /// <summary>
        /// Disassemble the method.
        /// </summary>
        /// <param name="method">A method to disassemble</param>
        /// <param name="builder">The builder to receive the method ASM</param>
        /// <param name="formatter">A Iced Formatter to use instead of the default <see cref="NasmFormatter"/></param>
        public static void ToAsm(this MethodInfo method, StringBuilder builder, Formatter formatter = null)
        {
            if (method == null) throw new ArgumentNullException(nameof(method));

            lock (initLock)
            {
                if (_runtime == null)
                {
                    var dataTarget = DataTarget.AttachToProcess(Process.GetCurrentProcess().Id, UInt32.MaxValue, AttachFlag.Passive);
                    var clrVersion = dataTarget.ClrVersions.First();
                    _runtime = clrVersion.CreateRuntime();
                }
            }

            // Make sure the method is being Jitted
            RuntimeHelpers.PrepareMethod(method.MethodHandle);

            // Get the handle from clrmd
            var clrmdMethodHandle = _runtime.GetMethodByHandle((ulong)method.MethodHandle.Value.ToInt64());

            if (clrmdMethodHandle.NativeCode == 0) throw new InvalidOperationException($"Unable to disassemble method `{method}`");

            //var check = clrmdMethodHandle.NativeCode;
            //var offsets = clrmdMethodHandle.ILOffsetMap;

            var codePtr = clrmdMethodHandle.HotColdInfo.HotStart;
            var codeSize = clrmdMethodHandle.HotColdInfo.HotSize;

            // Disassemble with Iced
            DecodeMethod(new IntPtr((long)codePtr), codeSize, builder, formatter);
        }

        private static void DecodeMethod(IntPtr ptr, uint size, StringBuilder builder, Formatter formatter = null)
        {
            // You can also pass in a hex string, eg. "90 91 929394", or you can use your own CodeReader
            // reading data from a file or memory etc
            var codeReader = new UnmanagedCodeReader(ptr, size);
            var decoder = Decoder.Create(IntPtr.Size * 8, codeReader);
            decoder.InstructionPointer = (ulong)ptr.ToInt64();
            ulong endRip = decoder.InstructionPointer + (uint)size;

            // This list is faster than List<Instruction> since it uses refs to the Instructions
            // instead of copying them (each Instruction is 32 bytes in size). It has a ref indexer,
            // and a ref iterator. Add() uses 'in' (ref readonly).
            var instructions = new InstructionList();
            while (decoder.InstructionPointer < endRip)
            {
                // The method allocates an uninitialized element at the end of the list and
                // returns a reference to it which is initialized by Decode().
                decoder.Decode(out instructions.AllocUninitializedElement());
            }

            // Formatters: Masm*, Nasm* and Gas* (AT&T)
            if (formatter == null)
            {
                formatter = new NasmFormatter();
                formatter.Options.DigitSeparator = "`";
                formatter.Options.FirstOperandCharIndex = 10;
            }
            var output = new StringBuilderFormatterOutput();
            // Use InstructionList's ref iterator (C# 7.3) to prevent copying 32 bytes every iteration
            foreach (ref var instr in instructions)
            {
                // Don't use instr.ToString(), it allocates more, uses masm syntax and default options
                formatter.Format(ref instr, output);
                builder.AppendLine($"{instr.IP64:X16} {output.ToStringAndReset()}");
            }
        }

        private class UnmanagedCodeReader : CodeReader
        {
            private readonly IntPtr _ptr;
            private readonly uint _size;
            private uint _offset;

            public UnmanagedCodeReader(IntPtr ptr, uint size)
            {
                _ptr = ptr;
                _size = size;
            }

            public override int ReadByte()
            {
                if (_offset >= _size)
                {
                    return -1;
                }

                var offset = _offset;
                _offset++;
                return Marshal.ReadByte(_ptr, (int)offset);
            }
        }
    }
}