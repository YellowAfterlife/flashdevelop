using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Diagnostics.CodeAnalysis;

namespace WeifenLuo.WinFormsUI.Docking
{
    internal interface IContentFocusManager
    {
        void Activate(IDockContent content);
        void GiveUpFocus(IDockContent content);
        void AddToList(IDockContent content);
        void RemoveFromList(IDockContent content);
    }

    partial class DockPanel
    {
        interface IFocusManager
        {
            void SuspendFocusTracking();
            void ResumeFocusTracking();
            bool IsFocusTrackingSuspended { get; }
            IDockContent ActiveContent { get; }
            DockPane ActivePane { get; }
            IDockContent ActiveDocument { get; }
            DockPane ActiveDocumentPane { get; }
        }

        class FocusManagerImpl : Component, IContentFocusManager, IFocusManager
        {
            class HookEventArgs : EventArgs
            {
                [SuppressMessage("Microsoft.Performance", "CA1823:AvoidUnusedPrivateFields")]
                public int HookCode;
                [SuppressMessage("Microsoft.Performance", "CA1823:AvoidUnusedPrivateFields")]
                public IntPtr wParam;
                public IntPtr lParam;
            }

            class LocalWindowsHook : IDisposable
            {
                // Internal properties
                IntPtr m_hHook = IntPtr.Zero;
                readonly NativeMethods.HookProc m_filterFunc = null;
                readonly Win32.HookType m_hookType;

                // Event delegate
                public delegate void HookEventHandler(object sender, HookEventArgs e);

                // Event: HookInvoked 
                public event HookEventHandler HookInvoked;
                protected void OnHookInvoked(HookEventArgs e)
                {
                    HookInvoked?.Invoke(this, e);
                }

                public LocalWindowsHook(Win32.HookType hook)
                {
                    m_hookType = hook;
                    m_filterFunc = this.CoreHookProc;
                }

                // Default filter function
                public IntPtr CoreHookProc(int code, IntPtr wParam, IntPtr lParam)
                {
                    if (code < 0)
                        return NativeMethods.CallNextHookEx(m_hHook, code, wParam, lParam);

                    // Let clients determine what to do
                    HookEventArgs e = new HookEventArgs();
                    e.HookCode = code;
                    e.wParam = wParam;
                    e.lParam = lParam;
                    OnHookInvoked(e);

                    // Yield to the next hook in the chain
                    return NativeMethods.CallNextHookEx(m_hHook, code, wParam, lParam);
                }

                // Install the hook
                public void Install()
                {
                    if (m_hHook != IntPtr.Zero)
                        Uninstall();

                    int threadId = NativeMethods.GetCurrentThreadId();
                    m_hHook = NativeMethods.SetWindowsHookEx(m_hookType, m_filterFunc, IntPtr.Zero, threadId);
                }

                // Uninstall the hook
                public void Uninstall()
                {
                    if (m_hHook != IntPtr.Zero)
                    {
                        NativeMethods.UnhookWindowsHookEx(m_hHook);
                        m_hHook = IntPtr.Zero;
                    }
                }

                ~LocalWindowsHook()
                {
                    Dispose(false);
                }

                public void Dispose()
                {
                    Dispose(true);
                    GC.SuppressFinalize(this);
                }

                protected virtual void Dispose(bool disposing)
                {
                    Uninstall();
                }
            }

            readonly LocalWindowsHook m_localWindowsHook;
            readonly LocalWindowsHook.HookEventHandler m_hookEventHandler;

            public FocusManagerImpl(DockPanel dockPanel)
            {
                m_dockPanel = dockPanel;
                if (!NativeMethods.ShouldUseWin32()) return;
                m_localWindowsHook = new LocalWindowsHook(Win32.HookType.WH_CALLWNDPROCRET);
                m_hookEventHandler = HookEventHandler;
                m_localWindowsHook.HookInvoked += m_hookEventHandler;
                m_localWindowsHook.Install();
            }

            readonly DockPanel m_dockPanel;
            public DockPanel DockPanel => m_dockPanel;

            bool m_disposed = false;
            protected override void Dispose(bool disposing)
            {
                lock (this)
                {
                    if (!m_disposed && disposing)
                    {
                        if (NativeMethods.ShouldUseWin32())
                        {
                            m_localWindowsHook.Dispose();
                        }
                        m_disposed = true;
                    }

                    base.Dispose(disposing);
                }
            }

            IDockContent m_contentActivating = null;

            IDockContent ContentActivating
            {
                get => m_contentActivating;
                set => m_contentActivating = value;
            }

            public void Activate(IDockContent content)
            {
                if (IsFocusTrackingSuspended)
                {
                    ContentActivating = content;
                    return;
                }
                if (content is null) return;
                DockContentHandler handler = content.DockHandler;
                if (handler.Form.IsDisposed) return; // Should not reach here, but better than throwing an exception
                if (ContentContains(content, handler.ActiveWindowHandle)) NativeMethods.SetFocus(handler.ActiveWindowHandle);
                if (!handler.Form.ContainsFocus)
                {
                    if (!handler.Form.SelectNextControl(handler.Form.ActiveControl, true, true, true, true))
                    // Since DockContent Form is not selectalbe, use Win32 SetFocus instead
                    // HACK: This isn't working so good.  doesn't matter because we do our own focusing. -NICK
                    {}// if (NativeMethods.ShouldUseWin32()) NativeMethods.SetFocus(handler.Form.Handle);
                }
            }

            readonly List<IDockContent> m_listContent = new List<IDockContent>();
            List<IDockContent> ListContent => m_listContent;

            public void AddToList(IDockContent content)
            {
                if (ListContent.Contains(content) || IsInActiveList(content))
                    return;

                ListContent.Add(content);
            }

            public void RemoveFromList(IDockContent content)
            {
                if (IsInActiveList(content))
                    RemoveFromActiveList(content);
                if (ListContent.Contains(content))
                    ListContent.Remove(content);
            }

            IDockContent m_lastActiveContent = null;

            IDockContent LastActiveContent
            {
                get => m_lastActiveContent;
                set => m_lastActiveContent = value;
            }

            bool IsInActiveList(IDockContent content)
            {
                return !(content.DockHandler.NextActive is null && LastActiveContent != content);
            }

            void AddLastToActiveList(IDockContent content)
            {
                IDockContent last = LastActiveContent;
                if (last == content)
                    return;

                DockContentHandler handler = content.DockHandler;

                if (IsInActiveList(content))
                    RemoveFromActiveList(content);

                handler.PreviousActive = last;
                handler.NextActive = null;
                LastActiveContent = content;
                if (last != null)
                    last.DockHandler.NextActive = LastActiveContent;
            }

            void RemoveFromActiveList(IDockContent content)
            {
                if (LastActiveContent == content)
                    LastActiveContent = content.DockHandler.PreviousActive;

                IDockContent prev = content.DockHandler.PreviousActive;
                IDockContent next = content.DockHandler.NextActive;
                if (prev != null)
                    prev.DockHandler.NextActive = next;
                if (next != null)
                    next.DockHandler.PreviousActive = prev;

                content.DockHandler.PreviousActive = null;
                content.DockHandler.NextActive = null;
            }

            public void GiveUpFocus(IDockContent content)
            {
                DockContentHandler handler = content.DockHandler;
                if (!handler.Form.ContainsFocus)
                    return;

                if (IsFocusTrackingSuspended)
                    DockPanel.DummyControl.Focus();

                if (LastActiveContent == content)
                {
                    IDockContent prev = handler.PreviousActive;
                    if (prev != null)
                        prev.DockHandler.Activate();
                    else if (ListContent.Count > 0)
                        ListContent[ListContent.Count - 1].DockHandler.Activate();
                }
                else if (LastActiveContent != null)
                    LastActiveContent.DockHandler.Activate();
                else if (ListContent.Count > 0)
                    ListContent[ListContent.Count - 1].DockHandler.Activate();
            }

            static bool ContentContains(IDockContent content, IntPtr hWnd)
            {
                Control control = Control.FromChildHandle(hWnd);
                for (Control parent = control; parent != null; parent = parent.Parent)
                    if (parent == content.DockHandler.Form)
                        return true;

                return false;
            }

            uint m_countSuspendFocusTracking = 0;
            public void SuspendFocusTracking()
            {
                m_countSuspendFocusTracking++;
                if (NativeMethods.ShouldUseWin32()) m_localWindowsHook.HookInvoked -= m_hookEventHandler;
            }

            public void ResumeFocusTracking()
            {
                if (m_countSuspendFocusTracking == 0) return;

                if (--m_countSuspendFocusTracking == 0)
                {
                    if (ContentActivating != null)
                    {
                        Activate(ContentActivating);
                        ContentActivating = null;
                    }
                    if (NativeMethods.ShouldUseWin32()) m_localWindowsHook.HookInvoked += m_hookEventHandler;
                    if (!InRefreshActiveWindow) RefreshActiveWindow();
                }
            }

            public bool IsFocusTrackingSuspended => m_countSuspendFocusTracking != 0;

            // Windows hook event handler
            void HookEventHandler(object sender, HookEventArgs e)
            {
                Win32.Msgs msg = (Win32.Msgs)Marshal.ReadInt32(e.lParam, IntPtr.Size * 3);

                if (msg == Win32.Msgs.WM_KILLFOCUS)
                {
                    IntPtr wParam = Marshal.ReadIntPtr(e.lParam, IntPtr.Size * 2);
                    DockPane pane = GetPaneFromHandle(wParam);
                    if (pane is null)
                        RefreshActiveWindow();
                }
                else if (msg == Win32.Msgs.WM_SETFOCUS)
                    RefreshActiveWindow();
            }

            DockPane GetPaneFromHandle(IntPtr hWnd)
            {
                Control control = Control.FromChildHandle(hWnd);

                IDockContent content = null;
                DockPane pane = null;
                for (; control != null; control = control.Parent)
                {
                    content = control as IDockContent;
                    if (content != null)
                        content.DockHandler.ActiveWindowHandle = hWnd;

                    if (content != null && content.DockHandler.DockPanel == DockPanel)
                        return content.DockHandler.Pane;

                    pane = control as DockPane;
                    if (pane != null && pane.DockPanel == DockPanel)
                        break;
                }

                return pane;
            }

            bool m_inRefreshActiveWindow = false;
            bool InRefreshActiveWindow => m_inRefreshActiveWindow;

            void RefreshActiveWindow()
            {
                SuspendFocusTracking();
                m_inRefreshActiveWindow = true;

                DockPane oldActivePane = ActivePane;
                IDockContent oldActiveContent = ActiveContent;
                IDockContent oldActiveDocument = ActiveDocument;

                SetActivePane();
                SetActiveContent();
                SetActiveDocumentPane();
                SetActiveDocument();
                DockPanel.AutoHideWindow.RefreshActivePane();

                ResumeFocusTracking();
                m_inRefreshActiveWindow = false;

                if (oldActiveContent != ActiveContent)
                    DockPanel.OnActiveContentChanged(EventArgs.Empty);
                if (oldActiveDocument != ActiveDocument)
                    DockPanel.OnActiveDocumentChanged(EventArgs.Empty);
                if (oldActivePane != ActivePane)
                    DockPanel.OnActivePaneChanged(EventArgs.Empty);
            }

            DockPane m_activePane = null;
            public DockPane ActivePane => m_activePane;

            void SetActivePane()
            {
                DockPane value = !NativeMethods.ShouldUseWin32() ? null : GetPaneFromHandle(NativeMethods.GetFocus());
                if (m_activePane == value)
                    return;

                m_activePane?.SetIsActivated(false);

                m_activePane = value;

                m_activePane?.SetIsActivated(true);
            }

            IDockContent m_activeContent = null;
            public IDockContent ActiveContent => m_activeContent;

            internal void SetActiveContent()
            {
                IDockContent value = ActivePane?.ActiveContent;

                if (m_activeContent == value)
                    return;

                m_activeContent?.DockHandler.SetIsActivated(false);

                m_activeContent = value;

                if (m_activeContent != null)
                {
                    m_activeContent.DockHandler.SetIsActivated(true);
                    if (!DockHelper.IsDockStateAutoHide((m_activeContent.DockHandler.DockState)))
                        AddLastToActiveList(m_activeContent);
                }
            }

            DockPane m_activeDocumentPane = null;
            public DockPane ActiveDocumentPane => m_activeDocumentPane;

            void SetActiveDocumentPane()
            {
                DockPane value = null;

                if (ActivePane != null && ActivePane.DockState == DockState.Document)
                    value = ActivePane;

                if (value is null && DockPanel.DockWindows != null)
                {
                    if (ActiveDocumentPane is null)
                        value = DockPanel.DockWindows[DockState.Document].DefaultPane;
                    else if (ActiveDocumentPane.DockPanel != DockPanel || ActiveDocumentPane.DockState != DockState.Document)
                        value = DockPanel.DockWindows[DockState.Document].DefaultPane;
                    else
                        value = ActiveDocumentPane;
                }

                if (m_activeDocumentPane == value)
                    return;

                m_activeDocumentPane?.SetIsActiveDocumentPane(false);

                m_activeDocumentPane = value;

                m_activeDocumentPane?.SetIsActiveDocumentPane(true);
            }

            IDockContent m_activeDocument = null;
            public IDockContent ActiveDocument => m_activeDocument;

            void SetActiveDocument()
            {
                IDockContent value = ActiveDocumentPane?.ActiveContent;

                if (m_activeDocument == value)
                    return;

                m_activeDocument = value;
            }
        }

        IFocusManager FocusManager => m_focusManager;

        internal IContentFocusManager ContentFocusManager => m_focusManager;

        internal void SaveFocus()
        {
            DummyControl.Focus();
        }

        [Browsable(false)]
        public IDockContent ActiveContent => FocusManager.ActiveContent;

        [Browsable(false)]
        public DockPane ActivePane => FocusManager.ActivePane;

        [Browsable(false)]
        public IDockContent ActiveDocument => FocusManager.ActiveDocument;

        [Browsable(false)]
        public DockPane ActiveDocumentPane => FocusManager.ActiveDocumentPane;

        static readonly object ActiveDocumentChangedEvent = new object();
        [LocalizedCategory("Category_PropertyChanged")]
        [LocalizedDescription("DockPanel_ActiveDocumentChanged_Description")]
        public event EventHandler ActiveDocumentChanged
        {
            add => Events.AddHandler(ActiveDocumentChangedEvent, value);
            remove => Events.RemoveHandler(ActiveDocumentChangedEvent, value);
        }
        protected virtual void OnActiveDocumentChanged(EventArgs e)
        {
            EventHandler handler = (EventHandler)Events[ActiveDocumentChangedEvent];
            handler?.Invoke(this, e);
        }

        static readonly object ActiveContentChangedEvent = new object();
        [LocalizedCategory("Category_PropertyChanged")]
        [LocalizedDescription("DockPanel_ActiveContentChanged_Description")]
        public event EventHandler ActiveContentChanged
        {
            add => Events.AddHandler(ActiveContentChangedEvent, value);
            remove => Events.RemoveHandler(ActiveContentChangedEvent, value);
        }
        protected void OnActiveContentChanged(EventArgs e)
        {
            EventHandler handler = (EventHandler)Events[ActiveContentChangedEvent];
            handler?.Invoke(this, e);
        }

        static readonly object ActivePaneChangedEvent = new object();
        [LocalizedCategory("Category_PropertyChanged")]
        [LocalizedDescription("DockPanel_ActivePaneChanged_Description")]
        public event EventHandler ActivePaneChanged
        {
            add => Events.AddHandler(ActivePaneChangedEvent, value);
            remove => Events.RemoveHandler(ActivePaneChangedEvent, value);
        }
        protected virtual void OnActivePaneChanged(EventArgs e)
        {
            EventHandler handler = (EventHandler)Events[ActivePaneChangedEvent];
            handler?.Invoke(this, e);
        }
    }
}
