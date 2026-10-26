using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace MoonSweepMods.ModLoader;

/// <summary>Native Windows "Open" dialog, run on its own STA thread so the game keeps rendering.</summary>
internal static class FilePicker
{
    private const int OFN_ALLOWMULTISELECT = 0x200;
    private const int OFN_NOCHANGEDIR = 0x8;
    private const int OFN_PATHMUSTEXIST = 0x800;
    private const int OFN_FILEMUSTEXIST = 0x1000;
    private const int OFN_EXPLORER = 0x80000;
    private const int BufferChars = 32 * 1024;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public IntPtr lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetOpenFileNameW")]
    private static extern bool GetOpenFileName(ref OpenFileName ofn);

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    /// <summary>Shows the dialog without blocking; <paramref name="done"/> runs on the picker thread.</summary>
    public static void Show(string title, string filter, string? initialDir, Action<List<string>> done)
    {
        var owner = GetActiveWindow();
        var thread = new Thread(() => done(Run(owner, title, filter, initialDir))) { IsBackground = true, Name = "Mod file picker" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static List<string> Run(IntPtr owner, string title, string filter, string? initialDir)
    {
        var result = new List<string>();
        var buffer = Marshal.AllocHGlobal(BufferChars * 2);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = owner,
                lpstrFilter = filter,
                lpstrFile = buffer,
                nMaxFile = BufferChars,
                lpstrInitialDir = initialDir,
                lpstrTitle = title,
                Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | OFN_ALLOWMULTISELECT,
            };
            if (!GetOpenFileName(ref ofn)) return result;

            // Single file: "C:\dir\file.zip\0\0". Several: "C:\dir\0a.zip\0b.zip\0\0".
            var parts = new List<string>();
            var offset = 0;
            while (true)
            {
                var s = Marshal.PtrToStringUni(buffer + offset * 2);
                if (string.IsNullOrEmpty(s)) break;
                parts.Add(s);
                offset += s.Length + 1;
            }
            if (parts.Count == 1) result.Add(parts[0]);
            else for (var i = 1; i < parts.Count; i++) result.Add(System.IO.Path.Combine(parts[0], parts[i]));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }
}
