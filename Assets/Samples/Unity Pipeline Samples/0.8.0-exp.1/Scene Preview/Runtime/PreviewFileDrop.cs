using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace Unity.Pipeline.Samples.ScenePreview
{
    // Drop a content archive (.ca) on the Windows player's window to preview it, e.g. one downloaded from
    // the pipeline's imports API. Unity players don't take dropped files themselves, so this enables
    // drops on the window (DragAcceptFiles) and reads WM_DROPFILES through a message hook on the main
    // thread. Windows standalone players only; elsewhere it does nothing. Added to the PreviewRig
    // automatically when the scene loads, so the scene needs no change.
    public class PreviewFileDrop : MonoBehaviour
    {
        /// <summary>True where dropping files on the window works (a Windows standalone player).</summary>
        public static bool Supported =>
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            true;
#else
            false;
#endif

        // Filled by the hook (a static method, so IL2CPP can call it from native code), drained in Update.
        static readonly ConcurrentQueue<string> s_Dropped = new();

        PreviewClient m_Client;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (!Supported)
                return;
            var client = FindFirstObjectByType<PreviewClient>();
            if (client != null && client.GetComponent<PreviewFileDrop>() == null)
                client.gameObject.AddComponent<PreviewFileDrop>();
        }

        void Awake() => m_Client = GetComponent<PreviewClient>();

        void OnEnable() => Hook();

        void OnDisable() => Unhook();

        void Update()
        {
            // Any file is tried, whatever its name: browsers save the pipeline's ".ca" artifact as plain
            // "ca" (they drop a leading dot). A file that isn't a content archive fails to mount, and the
            // loader reports that in the status line.
            while (s_Dropped.TryDequeue(out var path))
            {
                if (Directory.Exists(path))
                {
                    Debug.LogWarning($"[PreviewFileDrop] ignored folder '{path}': drop a content archive (.ca) file");
                    continue;
                }
                Debug.Log($"[PreviewFileDrop] previewing dropped '{path}'");
                m_Client.PreviewArchive(path);
            }
        }

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        const int WH_GETMESSAGE = 3;
        const uint WM_DROPFILES = 0x0233;

        [StructLayout(LayoutKind.Sequential)]
        struct Msg
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int x, y;
        }

        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        delegate bool EnumThreadWndProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint dwThreadId, EnumThreadWndProc lpfn, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("shell32.dll")] static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder lpszFile, uint cch);
        [DllImport("shell32.dll")] static extern void DragFinish(IntPtr hDrop);

        static IntPtr s_Hook;
        static IntPtr s_Window;
        static readonly HookProc s_HookProc = OnMessage;              // kept alive: native code holds it
        static readonly EnumThreadWndProc s_FindWindow = OnWindow;

        static void Hook()
        {
            if (s_Hook != IntPtr.Zero)
                return;
            s_Window = IntPtr.Zero;
            var thread = GetCurrentThreadId();
            EnumThreadWindows(thread, s_FindWindow, IntPtr.Zero);
            if (s_Window == IntPtr.Zero)
            {
                Debug.LogWarning("[PreviewFileDrop] couldn't find the player's window; drag and drop is off");
                return;
            }
            DragAcceptFiles(s_Window, true);
            s_Hook = SetWindowsHookEx(WH_GETMESSAGE, s_HookProc, IntPtr.Zero, thread);
            Debug.Log(s_Hook != IntPtr.Zero
                ? "[PreviewFileDrop] drop a .ca file on the window to preview it"
                : "[PreviewFileDrop] couldn't install the message hook; drag and drop is off");
        }

        static void Unhook()
        {
            if (s_Hook != IntPtr.Zero)
                UnhookWindowsHookEx(s_Hook);
            if (s_Window != IntPtr.Zero)
                DragAcceptFiles(s_Window, false);
            s_Hook = IntPtr.Zero;
        }

        // The player's main window is the thread's "UnityWndClass" window.
        [AOT.MonoPInvokeCallback(typeof(EnumThreadWndProc))]
        static bool OnWindow(IntPtr hwnd, IntPtr lParam)
        {
            var name = new StringBuilder(64);
            GetClassName(hwnd, name, name.Capacity);
            if (name.ToString() != "UnityWndClass")
                return true;
            s_Window = hwnd;
            return false;
        }

        [AOT.MonoPInvokeCallback(typeof(HookProc))]
        static IntPtr OnMessage(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var msg = Marshal.PtrToStructure<Msg>(lParam);
                if (msg.message == WM_DROPFILES)
                {
                    var drop = msg.wParam;
                    var count = DragQueryFile(drop, 0xFFFFFFFF, null, 0);
                    for (uint i = 0; i < count; i++)
                    {
                        var length = DragQueryFile(drop, i, null, 0);
                        var path = new StringBuilder((int)length + 1);
                        DragQueryFile(drop, i, path, (uint)path.Capacity);
                        s_Dropped.Enqueue(path.ToString());
                    }
                    DragFinish(drop);
                }
            }
            return CallNextHookEx(s_Hook, code, wParam, lParam);
        }
#else
        static void Hook() { }
        static void Unhook() { }
#endif
    }
}
