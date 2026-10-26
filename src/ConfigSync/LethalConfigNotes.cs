using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using BepInEx.Configuration;
using MonoMod.RuntimeDetour;

namespace ConfigSync;

// The host's value replaces what LethalConfig reads from the entry, so it never shows as an unsaved change or gets
// saved to the player's file. LethalConfig's item list is internal: this uses reflection and does nothing if it changes.
internal static class LethalConfigNotes
{
    private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static Hook? generateHook;
    private static Hook? originalValueHook;
    private static Hook? appearanceHook;
    private static Type? managerType;
    private static FieldInfo? controllerItemField;
    private static FieldInfo? tooltipField;
    private static PropertyInfo? entryProperty;
    private static PropertyInfo? currentValueProperty;
    private static PropertyInfo? originalValueProperty;

    public static void Attach()
    {
        var manager = Type.GetType("LethalConfig.LethalConfigManager, LethalConfig");
        // LethalConfig calls this every time the menu opens, after it creates the items and before it draws them.
        MethodInfo? generate = manager?.GetMethod("AutoGenerateMissingConfigsIfNeeded", Hidden);
        var item = Type.GetType("LethalConfig.ConfigItems.BaseConfigItem, LethalConfig");
        entryProperty = item?.GetProperty("BaseConfigEntry", Hidden);
        currentValueProperty = item?.GetProperty("CurrentBoxedValue", Hidden);
        originalValueProperty = item?.GetProperty("OriginalBoxedValue", Hidden);
        if (manager == null || generate == null || entryProperty == null || currentValueProperty == null ||
            originalValueProperty?.GetMethod == null)
        {
            return;
        }
        managerType = manager;
        HookTooltips();
        SyncedConfig.StateChanged += Push;
        originalValueHook = new Hook(originalValueProperty.GetMethod, (Func<Func<object, object>, object, object>)((orig, self) =>
            entryProperty.GetValue(self) is ConfigEntryBase entry && SyncedConfig.Find(entry)?.HostValue is { } host
                ? host
                : orig(self)));
        generateHook = new Hook(generate, (Action<Action>)(orig =>
        {
            orig();
            try
            {
                Mark(manager);
            }
            catch (Exception e)
            {
                Log.Source?.LogWarning($"Couldn't mark shared settings in LethalConfig: {e.Message}");
            }
        }));
    }

    private static void Mark(Type manager)
    {
        foreach ((object item, ISyncedEntry shared) in SharedItems(manager))
        {
            currentValueProperty!.SetValue(item, originalValueProperty!.GetValue(item));
            object? options = item.GetType().GetProperty("Options", Hidden)?.GetValue(item);
            PropertyInfo? callback = options?.GetType().GetProperty("CanModifyCallback");
            // Don't replace a callback the mod set itself, or the one we set on an earlier open.
            if (callback == null || callback.GetValue(options) != null)
            {
                continue;
            }
            callback.SetValue(options, new Note(shared).For(callback.PropertyType));
        }
    }

    private static IEnumerable<(object Item, ISyncedEntry Shared)> SharedItems(Type manager)
    {
        if (manager.GetProperty("Mods", Hidden)?.GetValue(null) is not IDictionary mods)
        {
            yield break;
        }
        foreach (object mod in mods.Values)
        {
            if (mod.GetType().GetField("ConfigItems", Hidden)?.GetValue(mod) is not IEnumerable items)
            {
                continue;
            }
            foreach (object item in items)
            {
                if (entryProperty!.GetValue(item) is ConfigEntryBase entry && SyncedConfig.Find(entry) is { } shared)
                {
                    yield return (item, shared);
                }
            }
        }
    }

    // Setting the item's value also redraws its row, which rewrites the tooltip through HookTooltips.
    private static void Push(ISyncedEntry changed)
    {
        if (managerType == null)
        {
            return;
        }
        try
        {
            foreach ((object item, ISyncedEntry shared) in SharedItems(managerType))
            {
                if (shared == changed)
                {
                    currentValueProperty!.SetValue(item, originalValueProperty!.GetValue(item));
                }
            }
        }
        catch (Exception e)
        {
            Log.Source?.LogWarning($"Couldn't update LethalConfig with the host's value: {e.Message}");
        }
    }

    // LethalConfig writes a row's tooltip once, when the row is made; rewrite it each time the row redraws.
    private static void HookTooltips()
    {
        var controller = Type.GetType("LethalConfig.MonoBehaviours.Components.ModConfigController, LethalConfig");
        controllerItemField = controller?.GetField("BaseConfigItem", Hidden);
        tooltipField = controller?.GetField("tooltipTrigger", Hidden);
        MethodInfo? appearance = controller?.GetMethod("UpdateAppearance", Hidden);
        if (controllerItemField == null || tooltipField?.FieldType.GetField("tooltipText", Hidden) == null || appearance == null)
        {
            return;
        }
        appearanceHook = new Hook(appearance, (Action<Action<object>, object>)((orig, self) =>
        {
            orig(self);
            try
            {
                if (controllerItemField.GetValue(self) is { } item && entryProperty!.GetValue(item) is ConfigEntryBase entry &&
                    SyncedConfig.Find(entry)?.HostNote is { } note && tooltipField.GetValue(self) is { } tooltip)
                {
                    tooltip.GetType().GetField("tooltipText", Hidden)!.SetValue(
                        tooltip, "<b>Modifying this entry is currently disabled:</b>\n<b>" + note + "</b>");
                }
            }
            catch (Exception e)
            {
                Log.Source?.LogWarning($"Couldn't update a LethalConfig tooltip: {e.Message}");
            }
        }));
    }

    private sealed class Note
    {
        private readonly ISyncedEntry shared;

        public Note(ISyncedEntry shared) => this.shared = shared;

        public string? Reason() => shared.HostNote;

        public Delegate For(Type delegateType)
        {
            // CanModifyResult.True() when the player's own value is in use, else CanModifyResult.False(reason).
            Type result = delegateType.GetMethod("Invoke")!.ReturnType;
            ParameterExpression reason = Expression.Variable(typeof(string), "reason");
            Expression body = Expression.Block(
                result,
                new[] { reason },
                Expression.Assign(reason, Expression.Call(Expression.Constant(this), nameof(Reason), null)),
                Expression.Condition(
                    Expression.Equal(reason, Expression.Constant(null, typeof(string))),
                    Expression.Call(result, "True", null),
                    Expression.Call(result, "False", null, reason)));
            return Expression.Lambda(delegateType, body).Compile();
        }
    }
}
