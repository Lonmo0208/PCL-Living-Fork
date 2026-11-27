// SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace PCLLF.Native.Facade.Tests;

// The projected glass menu can raise a folder menu item's Click for a row that is not a folder row
// (instance rows and placeholder rows leave Tag empty). RefreshClick/Delete/Rename/Open used to cast
// the row Tag straight to the folder type, so such a click ended in a NullReferenceException and took
// the launcher down; they now ignore a row without a folder instead.
internal static class SelectionMenuGuardChecks {
    internal static void Run() {
        Type folder = Native.Type("PCL.ModMinecraft+McFolder");
        Type instance = Native.Type("PCL.ModMinecraft+McInstance");
        // The controller/host live in the WPF core assembly, which this test host references only
        // through the real launcher entry assembly; resolve them from the loaded assemblies.
        Assembly core = AppDomain.CurrentDomain.GetAssemblies()
            .First(a => a.GetName().Name == "PCLLF.WPF.Core");
        Type hostType = core.GetType("PCLCS.FolderSelectionPageHost`2", true)!.MakeGenericType(folder, instance);
        object host = Activator.CreateInstance(hostType)!;
        var row = new PCL.MyListItem();
        Native.Field(hostType, "MenuRow").SetValue(host, new Func<object, FrameworkElement>(_ => row));

        int refreshes = 0;
        Native.Field(hostType, "ModpackEnabled").SetValue(host, new Func<bool>(() => false));
        Native.Field(hostType, "AnyLocked").SetValue(host, new Func<bool>(() => false));
        Native.Field(hostType, "Downloading").SetValue(host, new Func<bool, bool>(_ => false));
        Native.Field(hostType, "WriteIni").SetValue(host,
            new Action<string, string, object>((file, key, value) => refreshes++));
        Native.Field(hostType, "RefreshInstances").SetValue(host, new Action<bool>(_ => refreshes++));

        Type controller = core.GetType("PCLCS.FolderSelectionPageController`5", true)!.MakeGenericType(
            folder, Native.Type("PCL.ModMinecraft+McFolder+Types"), instance,
            Native.Type("PCL.ModMinecraft+McInstance+McInstanceState"), Native.Type("PCL.ModMinecraft+McInstanceCardType"));

        foreach (string name in new[] { "RefreshClick", "Delete", "Rename", "Open" }) {
            foreach (object? tag in new object?[] { null, "not-a-folder" }) {
                row.Tag = tag;
                try {
                    Native.Method(controller, name).Invoke(null, new object?[] { new object(), host });
                } catch (TargetInvocationException error) when (error.InnerException is not null) {
                    throw new InvalidOperationException(
                        $"{name} threw for a row whose Tag is {(tag is null ? "null" : "not a folder")} "
                        + $"({error.InnerException.GetType().Name}: {error.InnerException.Message})", error.InnerException);
                }
            }
        }

        Native.Check(refreshes == 0, "A folder menu action ran for a row that carries no folder.");

        // 空菜单行（MenuRow 返回 null）也不能在取值时崩。
        row.Tag = null;
        Native.Field(hostType, "MenuRow").SetValue(host, new Func<object, FrameworkElement>(_ => null!));
        Native.Method(controller, "RefreshClick").Invoke(null, new object?[] { new object(), host });

        // 玻璃侧栏的行是重建的 Button：投影菜单必须用原始行（ActionTarget）定位动作，而不是按钮。
        object folderInstance = Activator.CreateInstance(folder)!;
        Native.Field(folder, "Name").SetValue(folderInstance, "awa8-21");
        Native.Field(folder, "Location").SetValue(folderInstance, "E:\\awa8-21\\.minecraft\\");
        var originalMenu = new ContextMenu();
        var refreshItem = new MenuItem { Header = "刷新" };
        object? resolved = null;
        refreshItem.Click += (sender, _) => resolved = Native.Static("PCL.PageSelectLeft", "NativeMenuRow", sender);
        originalMenu.Items.Add(refreshItem);
        var originalRow = new PCL.MyListItem { Tag = folderInstance };
        var glassButton = new Button();
        Type menuType = Native.Type("PCL.GlassActionMenu");
        MethodInfo fromMenu = menuType.GetMethod("FromContextMenu", Native.Flags, null,
            new[] { typeof(ContextMenu), typeof(FrameworkElement), typeof(bool), typeof(bool), typeof(FrameworkElement) }, null)!;
        var projected = (ContextMenu)fromMenu.Invoke(null,
            new object?[] { originalMenu, glassButton, false, false, originalRow })!;
        var projectedItem = (MenuItem)projected.Items[0];
        projectedItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, projectedItem));
        Native.Check(resolved is PCL.MyListItem fromGlass && ReferenceEquals(fromGlass.Tag, folderInstance),
            "A projected glass row did not resolve back to its original row for menu actions.");

        Console.WriteLine("PASS selection menu guards: folder actions ignore rows without a folder (Tag null/foreign type/absent row) instead of a NullReferenceException, and a projected glass row resolves to its original row.");
    }
}
