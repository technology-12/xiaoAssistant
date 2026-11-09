// net40 垫片：System.Threading.Volatile（.NET 4.5 才有）。
// Read = 全栅栏后读；Write = 写后全栅栏。语义与 4.5 Volatile 等价（x86/x64 均适用）。

using System;

namespace System.Threading
{
    public static class Volatile
    {
        public static bool Read(ref bool location) { var value = location; Thread.MemoryBarrier(); return value; }
        public static byte Read(ref byte location) { var value = location; Thread.MemoryBarrier(); return value; }
        public static sbyte Read(ref sbyte location) { var value = location; Thread.MemoryBarrier(); return value; }
        public static short Read(ref short location) { var value = location; Thread.MemoryBarrier(); return value; }
        public static int Read(ref int location) { var value = location; Thread.MemoryBarrier(); return value; }
        public static long Read(ref long location) { var value = location; Thread.MemoryBarrier(); return value; }
        public static double Read(ref double location) { var value = location; Thread.MemoryBarrier(); return value; }
        public static float Read(ref float location) { var value = location; Thread.MemoryBarrier(); return value; }
        public static IntPtr Read(ref IntPtr location) { var value = location; Thread.MemoryBarrier(); return value; }
        public static UIntPtr Read(ref UIntPtr location) { var value = location; Thread.MemoryBarrier(); return value; }
        public static object? Read(ref object? location) { var value = location; Thread.MemoryBarrier(); return value; }
        public static T Read<T>(ref T location) where T : class? { var value = location; Thread.MemoryBarrier(); return value; }

        public static void Write(ref bool location, bool value) { location = value; Thread.MemoryBarrier(); }
        public static void Write(ref byte location, byte value) { location = value; Thread.MemoryBarrier(); }
        public static void Write(ref sbyte location, sbyte value) { location = value; Thread.MemoryBarrier(); }
        public static void Write(ref short location, short value) { location = value; Thread.MemoryBarrier(); }
        public static void Write(ref int location, int value) { location = value; Thread.MemoryBarrier(); }
        public static void Write(ref long location, long value) { location = value; Thread.MemoryBarrier(); }
        public static void Write(ref double location, double value) { location = value; Thread.MemoryBarrier(); }
        public static void Write(ref float location, float value) { location = value; Thread.MemoryBarrier(); }
        public static void Write(ref IntPtr location, IntPtr value) { location = value; Thread.MemoryBarrier(); }
        public static void Write(ref UIntPtr location, UIntPtr value) { location = value; Thread.MemoryBarrier(); }
        public static void Write(ref object? location, object? value) { location = value; Thread.MemoryBarrier(); }
        public static void Write<T>(ref T location, T value) where T : class? { location = value; Thread.MemoryBarrier(); }
    }
}
