using System;
using System.Runtime.InteropServices;

namespace CUE4Parse.ACL
{
    public static class ACLNative
    {
        public const string LIB_NAME = "CUE4Parse-Natives";

        public static unsafe IntPtr nAllocate(int size, int alignment = 16)
        {
            return (IntPtr) NativeMemory.AlignedAlloc((nuint) size, (nuint) alignment);
        }

        public static unsafe void nDeallocate(IntPtr ptr, int size)
        {
            NativeMemory.AlignedFree((void*) ptr);
        }

        // pure c# way:
        //var rawPtr = Marshal.AllocHGlobal(size + 8);
        //var aligned = new IntPtr(16 * (((long) rawPtr + 15) / 16));
    }
}