using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Game.Main.Distribution.Editor
{
    /// <summary>捕获指定已启动 Player 的原始窗口像素；不激活窗口，不修改游戏状态。</summary>
    public static class OutpostPlayerWindowCapture
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct WindowRect { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public uint size;
            public int width, height;
            public ushort planes, bitCount;
            public uint compression, imageSize;
            public int xPixels, yPixels;
            public uint colorsUsed, colorsImportant;
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr window, out WindowRect rect);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] data, ref BitmapInfo info, uint colorMode);

        public static string Capture(int playerProcessId, string destination)
        {
            using var process = Process.GetProcessById(playerProcessId);
            if (!string.Equals(Path.GetFileName(process.MainModule.FileName), "Outpost.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("指定进程并非 Outpost.exe。");
            IntPtr window = process.MainWindowHandle;
            if (window == IntPtr.Zero || !GetWindowRect(window, out var rect))
                throw new InvalidOperationException("Player 窗口尚未建立。");
            int width = rect.right - rect.left;
            int height = rect.bottom - rect.top;
            if (width < 100 || height < 100 || width > 8192 || height > 8192)
                throw new InvalidOperationException("Player 窗口尺寸不适合截图。");
            IntPtr source = GetWindowDC(window);
            IntPtr memory = CreateCompatibleDC(source);
            IntPtr bitmap = CreateCompatibleBitmap(source, width, height);
            IntPtr previous = SelectObject(memory, bitmap);
            try
            {
                if (!PrintWindow(window, memory, 2)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var info = new BitmapInfo
                {
                    size = (uint)Marshal.SizeOf<BitmapInfo>(), width = width, height = height,
                    planes = 1, bitCount = 32, imageSize = (uint)(width * height * 4),
                };
                var pixels = new byte[width * height * 4];
                SelectObject(memory, previous);
                previous = IntPtr.Zero;
                if (GetDIBits(memory, bitmap, 0, (uint)height, pixels, ref info, 0) == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                // GDI 的 BGRX 正高度位图与 Texture2D 都按底行起存；只转换像素格式，不裁切、缩放或修图。
                var rgb = new byte[width * height * 3];
                for (int i = 0, j = 0; i < pixels.Length; i += 4, j += 3)
                {
                    rgb[j] = pixels[i + 2];
                    rgb[j + 1] = pixels[i + 1];
                    rgb[j + 2] = pixels[i];
                }
                var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                try
                {
                    texture.LoadRawTextureData(rgb);
                    texture.Apply();
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination)));
                    File.WriteAllBytes(destination, texture.EncodeToPNG());
                }
                finally { UnityEngine.Object.DestroyImmediate(texture); }
                return destination;
            }
            finally
            {
                if (previous != IntPtr.Zero) SelectObject(memory, previous);
                DeleteObject(bitmap);
                DeleteDC(memory);
                ReleaseDC(window, source);
            }
        }
    }
}
