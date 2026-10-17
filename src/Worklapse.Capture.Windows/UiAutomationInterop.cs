using System.Runtime.InteropServices;

namespace Worklapse.Capture.Windows;

// The few parts of the UI Automation client COM API (UIAutomationClient.h) that reading a browser's address bar
// needs, used directly rather than through WPF's System.Windows.Automation, which would pull the WindowsDesktop
// framework into the recorder. COM vtables are positional: every method up to the last one used is declared, in
// order; the ones never called are placeholders.

[ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
internal class CUIAutomation;

[ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomation
{
    void CompareElements();
    void CompareRuntimeIds();
    void GetRootElement();
    IUIAutomationElement ElementFromHandle(nint hwnd);
    void ElementFromPoint();
    void GetFocusedElement();
    void GetRootElementBuildCache();
    void ElementFromHandleBuildCache();
    void ElementFromPointBuildCache();
    void GetFocusedElementBuildCache();
    void CreateTreeWalker();
    void get_ControlViewWalker();
    void get_ContentViewWalker();
    void get_RawViewWalker();
    void get_RawViewCondition();
    void get_ControlViewCondition();
    void get_ContentViewCondition();
    void CreateCacheRequest();
    void CreateTrueCondition();
    void CreateFalseCondition();
    IUIAutomationCondition CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value);
    void CreatePropertyConditionEx();
    IUIAutomationCondition CreateAndCondition(IUIAutomationCondition condition1, IUIAutomationCondition condition2);
}

[ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElement
{
    void SetFocus();
    void GetRuntimeId();

    /// <summary>The first match in tree order, or null.</summary>
    IUIAutomationElement? FindFirst(int treeScope, IUIAutomationCondition condition);

    void FindAll();
    void FindFirstBuildCache();
    void FindAllBuildCache();
    void BuildUpdatedCache();

    [return: MarshalAs(UnmanagedType.Struct)]
    object? GetCurrentPropertyValue(int propertyId);
}

[ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationCondition;

internal static class Uia
{
    public const int TreeScopeDescendants = 4;

    public const int ControlTypePropertyId = 30003;
    public const int NamePropertyId = 30005;
    public const int HasKeyboardFocusPropertyId = 30008;
    public const int IsOffscreenPropertyId = 30022;
    public const int AutomationIdPropertyId = 30011;
    public const int ClassNamePropertyId = 30012;
    public const int ValueValuePropertyId = 30045;

    public const int DocumentControlTypeId = 50030;
}
