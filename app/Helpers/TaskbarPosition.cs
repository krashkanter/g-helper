using System.Runtime.InteropServices;

namespace GHelper.Helpers
{
    public enum TaskbarEdge
    {
        Left = 0,
        Top = 1,
        Right = 2,
        Bottom = 3
    }

    /// <summary>
    /// Places G-Helper windows next to the taskbar on whichever screen edge it is docked,
    /// instead of always using the bottom-right corner.
    /// </summary>
    public static class TaskbarPosition
    {
        const uint ABM_GETTASKBARPOS = 0x00000005;

        const int ScreenMargin = 10;
        const int ChildGap = 5;

        [StructLayout(LayoutKind.Sequential)]
        struct APPBARDATA
        {
            public uint cbSize;
            public nint hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public nint lParam;
        }

        [DllImport("shell32.dll")]
        static extern nuint SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        static TaskbarEdge? edge;

        /// <summary>
        /// Screen edge the taskbar is docked to. It is read once when the app starts (see Init),
        /// so moving the taskbar while G-Helper is running takes effect after the app is reopened.
        /// </summary>
        public static TaskbarEdge Edge => edge ??= DetectEdge();

        /// <summary>
        /// Reads the taskbar edge for this app session. Called once from Program.Main.
        /// </summary>
        public static void Init()
        {
            edge = DetectEdge();
            Logger.WriteLine($"Taskbar edge: {edge}");
        }

        /// <summary>
        /// Asks Explorer where the taskbar is docked. Needs no admin rights and works for auto-hide taskbars too.
        /// </summary>
        static TaskbarEdge DetectEdge()
        {
            var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
            if (SHAppBarMessage(ABM_GETTASKBARPOS, ref data) != 0 && data.uEdge <= (uint)TaskbarEdge.Bottom)
                return (TaskbarEdge)data.uEdge;

            // Explorer did not answer (e.g. it is restarting): infer the edge from the space the taskbar reserves
            Screen? screen = Screen.PrimaryScreen;
            if (screen is null) return TaskbarEdge.Bottom;

            Rectangle bounds = screen.Bounds, work = screen.WorkingArea;
            if (work.Left > bounds.Left) return TaskbarEdge.Left;
            if (work.Top > bounds.Top) return TaskbarEdge.Top;
            if (work.Right < bounds.Right) return TaskbarEdge.Right;
            return TaskbarEdge.Bottom;
        }

        /// <summary>
        /// Main window location: the corner where the taskbar's tray sits.
        /// Bottom/right taskbar: bottom-right. Top taskbar: top-right. Left taskbar: bottom-left.
        /// </summary>
        public static Point MainWindowLocation(Screen screen, Size size)
        {
            Rectangle work = screen.WorkingArea;

            int left = Edge == TaskbarEdge.Left ? work.Left + ScreenMargin : work.Right - ScreenMargin - size.Width;
            int top = Edge == TaskbarEdge.Top ? work.Top + ScreenMargin : work.Bottom - ScreenMargin - size.Height;

            return new Point(left, top);
        }

        /// <summary>
        /// Left coordinate for a window opened beside the main window (fans, extra, matrix...).
        /// It opens on the side of the main window that faces the middle of the screen, so it stays on screen.
        /// </summary>
        public static int ChildLeft(Form child)
        {
            Form main = Program.settingsForm;
            Rectangle work = Screen.FromControl(main).WorkingArea;

            bool mainOnLeftHalf = main.Left + main.Width / 2 < work.Left + work.Width / 2;
            return mainOnLeftHalf ? main.Right + ChildGap : main.Left - child.Width - ChildGap;
        }
    }
}
