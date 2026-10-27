using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace InfoPanel.Native;

/// <summary>Ícono de cualquier elemento del shell (apps de escritorio y de la Store) como PNG con transparencia.</summary>
internal static class ShellImage
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int Cx;
        public int Cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int size, out BITMAP bitmap);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;

    /// <summary>Debe llamarse desde un hilo STA.</summary>
    public static byte[]? GetPng(string parsingName, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        IShellItemImageFactory factory;
        try
        {
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out factory);
        }
        catch
        {
            return null;
        }

        try
        {
            if (factory.GetImage(new SIZE { Cx = size, Cy = size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out var hbmp) != 0) return null;
            try
            {
                GetObject(hbmp, Marshal.SizeOf<BITMAP>(), out var bm);
                if (bm.BitsPixel != 32 || bm.Bits == IntPtr.Zero) return null;
                // Se copian los píxeles tal cual para conservar la transparencia (FromHbitmap la pierde)
                using var view = new Bitmap(bm.Width, bm.Height, bm.WidthBytes, PixelFormat.Format32bppPArgb, bm.Bits);
                using var copy = new Bitmap(view);
                // Los DIB del shell vienen de abajo hacia arriba
                copy.RotateFlip(RotateFlipType.RotateNoneFlipY);
                using var ms = new MemoryStream();
                copy.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
            finally
            {
                DeleteObject(hbmp);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }
}
