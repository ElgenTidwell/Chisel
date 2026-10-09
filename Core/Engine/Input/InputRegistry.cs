using Engine.Utils.Settings;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Engine.Input;
public static class InputRegistry
{
    private static string savePath => $"{Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create)}/{GameSettings.GameName}/keys.cfg";
    private static readonly Dictionary<string, InputBinding> bindings = new();

    internal static void Register(InputBinding binding)
    {
        if (bindings.ContainsKey(binding.Id))
            throw new InvalidOperationException($"Duplicate InputBinding id: '{binding.Id}'");
        bindings[binding.Id] = binding;
    }

    public static InputBinding? Get(string id) =>
        bindings.GetValueOrDefault(id);

    public static IEnumerable<string> GetCategories() =>
        bindings.Values.Select(b => b.Category).Distinct();

    public static IEnumerable<InputBinding> GetByCategory(string category) =>
        bindings.Values.Where(b => b.Category == category);
    public static void Save()
    {
        var data = bindings.ToDictionary(
            kvp => kvp.Key,
            kvp => new SavedBinding
            {
                Primary = BindingData.FromBinding(kvp.Value.GetPrimary()),
                Secondary = kvp.Value.GetSecondary() is { } s
                                ? BindingData.FromBinding(s) : null
            });

        File.WriteAllText(savePath, JsonConvert.SerializeObject(data, Formatting.Indented));
    }
    public static void Load()
    {
        if (!File.Exists(savePath)) return;

        var data = JsonConvert.DeserializeObject<Dictionary<string, SavedBinding>>(
            File.ReadAllText(savePath));

        if (data == null) return;

        foreach (var (id, saved) in data)
        {
            if (!bindings.TryGetValue(id, out var binding)) continue;

            try
            {
                binding.Rebind(saved.Primary.ToBinding(), isPrimary: true);
                if (saved.Secondary != null)
                    binding.Rebind(saved.Secondary.ToBinding(), isPrimary: false);
            }
            catch { /* corrupted entry, leave at default */ }
        }
    }
}
public class BindingData
{
    public string Type { get; set; } = "";
    public string Value { get; set; } = "";

    public static BindingData FromBinding(SingleInputBinding b) => b switch
    {
        BoundKey k => new BindingData { Type = "key", Value = k.Key.ToString() },
        BoundMouseButton m => new BindingData { Type = "mouse", Value = m.MouseButton.ToString() },
        BoundGamepadButton g => new BindingData { Type = "gamepad", Value = g.GamepadButton.ToString() },
        _ => null
    };

    public SingleInputBinding ToBinding() => Type switch
    {
        "key" => new BoundKey { Key = Enum.Parse<Keys>(Value) },
        "mouse" => new BoundMouseButton { MouseButton = Enum.Parse<MouseButton>(Value) },
        "gamepad" => new BoundGamepadButton(0, Enum.Parse<Buttons>(Value)),
        _ => default
    };
}
public class SavedBinding
{
    public BindingData Primary { get; set; }
    public BindingData? Secondary { get; set; }
}