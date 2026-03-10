using System;
using System.Runtime.InteropServices;
using static CUE4Parse.ACL.ACLNative;

namespace CUE4Parse.ACL
{
    public class CompressedTracks
    {
        public IntPtr Handle { get; private set; }
        private readonly int _bufferLength;

        public CompressedTracks(byte[] buffer)
        {
            _bufferLength = buffer.Length;
            Handle = nAllocate(_bufferLength);
            Marshal.Copy(buffer, 0, Handle, buffer.Length);
            var error = IsValid(false);
            if (error != null)
            {
                nDeallocate(Handle, _bufferLength);
                Handle = IntPtr.Zero;
                throw new ACLException(error);
            }
        }

        public CompressedTracks(IntPtr existing)
        {
            _bufferLength = -1;
            Handle = existing;
        }

        ~CompressedTracks()
        {
            if (_bufferLength >= 0 && Handle != IntPtr.Zero)
            {
                nDeallocate(Handle, _bufferLength);
                Handle = IntPtr.Zero;
            }
        }

        public string? IsValid(bool checkHash)
        {
            try
            {
                var error = Marshal.PtrToStringAnsi(nCompressedTracks_IsValid(Handle, checkHash))!;
                return error.Length > 0 ? error : null;
            }
            catch (EntryPointNotFoundException)
            {
                return "ACL native entry point nCompressedTracks_IsValid was not found";
            }
            catch (DllNotFoundException)
            {
                return "ACL native library CUE4Parse-Natives was not found";
            }
        }

        public TracksHeader GetTracksHeader() => Marshal.PtrToStructure<TracksHeader>(Handle + Marshal.SizeOf<RawBufferHeader>());
        public void SetDefaultScale(uint scale) => nTracksHeader_SetDefaultScale(Handle + Marshal.SizeOf<RawBufferHeader>(), scale);

        [DllImport(LIB_NAME)]
        private static extern IntPtr nCompressedTracks_IsValid(IntPtr handle, bool checkHash);

        [DllImport(LIB_NAME)]
        private static extern IntPtr nTracksHeader_SetDefaultScale(IntPtr handle, uint scale);
    }
}
