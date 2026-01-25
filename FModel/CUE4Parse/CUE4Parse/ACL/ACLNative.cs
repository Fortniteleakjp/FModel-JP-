﻿using System;
using System.Runtime.InteropServices;

namespace CUE4Parse.ACL
{
    public static class ACLNative
    {
        public const string LIB_NAME = "CUE4Parse-Natives";

        public static IntPtr nAllocate(int size, int alignment = 16)
        {
            return Marshal.AllocHGlobal(size);
        }

        public static void nDeallocate(IntPtr ptr, int size)
        {
            Marshal.FreeHGlobal(ptr);
        }

        // pure c# way:
        //var rawPtr = Marshal.AllocHGlobal(size + 8);
        //var aligned = new IntPtr(16 * (((long) rawPtr + 15) / 16));
    }
}