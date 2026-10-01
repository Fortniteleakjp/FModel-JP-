using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace FModel.Services.AssetEditing;

/// <summary>
/// applies the difference between the json FModel displayed and the json the user edited onto a UAssetAPI asset
/// only what changed is touched, everything else keeps the bytes UAssetAPI read
/// </summary>
public sealed class AssetJsonPatcher
{
    private readonly UAsset _asset;
    private readonly PackageReferenceResolver _references;
    private readonly AssetEditReport _report;

    public AssetJsonPatcher(UAsset asset, PackageReferenceResolver references, AssetEditReport report)
    {
        _asset = asset;
        _references = references;
        _report = report;
    }

    public void Apply(JArray original, JArray edited, int exportStart)
    {
        if (original.Count != edited.Count)
        {
            _report.Error(null, $"エクスポートの数が変わっています（{original.Count} → {edited.Count}）。エクスポートの追加・削除には対応していません");
            return;
        }

        for (var i = 0; i < original.Count; i++)
        {
            if (JToken.DeepEquals(original[i], edited[i])) continue;

            var exportIndex = exportStart + i;
            var path = $"[{i}]";
            if (original[i] is not JObject oe || edited[i] is not JObject ee)
            {
                _report.Error(path, "エクスポートはオブジェクトのままにしてください");
                continue;
            }
            if (exportIndex >= _asset.Exports.Count)
            {
                _report.Error(path, "対応するエクスポートが見つかりません");
                continue;
            }

            var export = _asset.Exports[exportIndex];
            var name = oe["Name"]?.ToString();
            if (name != null && !string.Equals(name, export.ObjectName?.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                _report.Error(path, $"エクスポート名が一致しません（JSON: {name} / uasset: {export.ObjectName}）");
                continue;
            }

            PatchExport(export, oe, ee, name ?? path);
        }
    }

    private void PatchExport(Export export, JObject original, JObject edited, string path)
    {
        foreach (var key in Keys(original, edited))
        {
            var o = original[key];
            var e = edited[key];
            if (JToken.DeepEquals(o, e)) continue;

            var keyPath = $"{path}.{key}";
            switch (key)
            {
                case "Properties" when export is NormalExport normal:
                    PatchContainer(normal.Data, o as JObject ?? [], e as JObject ?? [], keyPath, normal.GetClassTypeForAncestry(_asset, out _)?.ToString());
                    break;
                case "Rows" when export is DataTableExport table:
                    PatchRows(table, o as JObject ?? [], e as JObject ?? [], keyPath);
                    break;
                case "StringTable" when export is StringTableExport stringTable:
                    PatchStringTable(stringTable, o as JObject ?? [], e as JObject ?? [], keyPath);
                    break;
                default:
                    _report.Error(keyPath, export is RawExport
                        ? "このエクスポートは UAssetAPI で解析できないため書き換えられません"
                        : "この項目の書き換えには対応していません（Properties / Rows / StringTable のみ）");
                    break;
            }
        }
    }

    #region containers

    private void PatchContainer(List<PropertyData> data, JObject original, JObject edited, string path, string schemaName)
    {
        foreach (var key in Keys(original, edited))
        {
            var o = original[key];
            var e = edited[key];
            if (JToken.DeepEquals(o, e)) continue;

            var keyPath = $"{path}.{key}";
            var (name, arrayIndex) = ParseKey(key);
            var index = data.FindIndex(p => p.ArrayIndex == arrayIndex && string.Equals(p.Name?.ToString(), name, StringComparison.OrdinalIgnoreCase));

            if (e == null)
            {
                if (index >= 0)
                {
                    data.RemoveAt(index);
                    _report.Change(keyPath, "削除（既定値に戻ります）");
                }
                continue;
            }

            if (index < 0)
            {
                var created = CreateProperty(name, arrayIndex, schemaName, keyPath);
                if (created == null) continue;

                // a fresh property starts from its zero value, everything the user wrote is a change from there
                if (SetValue(created, null, e, keyPath))
                {
                    data.Add(created);
                    _report.Change(keyPath, "追加");
                }
                continue;
            }

            SetValue(data[index], o, e, keyPath);
        }
    }

    private void PatchRows(DataTableExport table, JObject original, JObject edited, string path)
    {
        var rows = table.Table.Data;
        var rowStruct = table.Table.Data.FirstOrDefault()?.StructType?.ToString();
        foreach (var key in Keys(original, edited))
        {
            var o = original[key];
            var e = edited[key];
            if (JToken.DeepEquals(o, e)) continue;

            var keyPath = $"{path}.{key}";
            var index = rows.FindIndex(r => string.Equals(r.Name?.ToString(), key, StringComparison.OrdinalIgnoreCase));
            if (e == null)
            {
                if (index >= 0)
                {
                    rows.RemoveAt(index);
                    _report.Change(keyPath, "行を削除");
                }
                continue;
            }

            if (e is not JObject editedRow)
            {
                _report.Error(keyPath, "行はオブジェクトで指定してください");
                continue;
            }

            if (index >= 0)
            {
                PatchContainer(rows[index].Value, o as JObject ?? [], editedRow, keyPath, rows[index].StructType?.ToString());
                continue;
            }

            // new row: start from a copy of an existing one so the struct type and guid are right, then diff against it
            if (rows.Count == 0)
            {
                _report.Error(keyPath, "空のデータテーブルには行を追加できません（行の構造が分からないため）");
                continue;
            }

            var template = rows[0];
            var row = (StructPropertyData) DeepClone(template);
            row.Name = new FName(_asset, key);
            var templateJson = original[template.Name.ToString()] as JObject ?? [];
            PatchContainer(row.Value, templateJson, editedRow, keyPath, rowStruct);
            rows.Add(row);
            _report.Change(keyPath, "行を追加");
        }
    }

    private void PatchStringTable(StringTableExport export, JObject original, JObject edited, string path)
    {
        foreach (var key in Keys(original, edited))
        {
            var o = original[key];
            var e = edited[key];
            if (JToken.DeepEquals(o, e)) continue;

            var keyPath = $"{path}.{key}";
            switch (key)
            {
                case "TableNamespace":
                    export.Table.TableNamespace = new FString(e?.ToString() ?? string.Empty);
                    _report.Change(keyPath, "変更");
                    break;
                case "KeysToEntries":
                {
                    var oe = o as JObject ?? [];
                    var ee = e as JObject ?? [];
                    foreach (var entry in Keys(oe, ee))
                    {
                        if (JToken.DeepEquals(oe[entry], ee[entry])) continue;
                        var entryKey = new FString(entry);
                        if (ee[entry] == null)
                        {
                            export.Table.Remove(entryKey);
                            _report.Change($"{keyPath}.{entry}", "削除");
                        }
                        else
                        {
                            export.Table[entryKey] = new FString(ee[entry].ToString());
                            _report.Change($"{keyPath}.{entry}", "変更");
                        }
                    }
                    break;
                }
                default:
                    _report.Error(keyPath, "この項目の書き換えには対応していません");
                    break;
            }
        }
    }

    #endregion

    #region values

    /// <summary>
    /// <paramref name="original"/> is null for freshly created properties
    /// </summary>
    private bool SetValue(PropertyData property, JToken original, JToken edited, string path)
    {
        try
        {
            var changed = SetValueCore(property, original, edited, path);
            if (changed) _report.Change(path, Describe(edited));
            return true;
        }
        catch (OverflowException)
        {
            _report.Error(path, $"値がこの型の範囲を超えています（型: {property.PropertyType}）");
            return false;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or ArgumentException or InvalidOperationException)
        {
            _report.Error(path, $"{ex.Message}（型: {property.PropertyType}）");
            return false;
        }
    }

    /// <returns>true when this call itself changed a leaf value (containers report their children instead)</returns>
    private bool SetValueCore(PropertyData property, JToken original, JToken edited, string path)
    {
        switch (property)
        {
            case BoolPropertyData b:
                b.Value = edited.Type == JTokenType.Boolean ? edited.Value<bool>() : throw new FormatException("true / false で指定してください");
                return true;
            case Int8PropertyData i8:
                i8.Value = checked((sbyte) Integer(edited));
                return true;
            case Int16PropertyData i16:
                i16.Value = checked((short) Integer(edited));
                return true;
            case IntPropertyData i32:
                i32.Value = checked((int) Integer(edited));
                return true;
            case Int64PropertyData i64:
                i64.Value = Integer(edited);
                return true;
            case UInt16PropertyData u16:
                u16.Value = checked((ushort) Integer(edited));
                return true;
            case UInt32PropertyData u32:
                u32.Value = checked((uint) Integer(edited));
                return true;
            case UInt64PropertyData u64:
                u64.Value = ulong.Parse(edited.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture);
                return true;
            case FloatPropertyData f:
                f.Value = (float) Number(edited);
                return true;
            case DoublePropertyData d:
                d.Value = Number(edited);
                return true;
            case BytePropertyData by:
                if (edited.Type == JTokenType.String)
                {
                    by.ByteType = BytePropertyType.FName;
                    by.EnumValue = EnumName(by.EnumType, edited.ToString(), by.EnumValue);
                }
                else
                {
                    if (by.ByteType == BytePropertyType.FName) throw new FormatException("列挙子の名前（\"EEnum::Value\"）で指定してください");
                    by.Value = checked((byte) Integer(edited));
                }
                return true;
            case EnumPropertyData en:
                en.Value = edited.Type == JTokenType.Null ? null : EnumName(en.EnumType, edited.ToString(), en.Value);
                return true;
            case NamePropertyData n:
                n.Value = Name(edited.Type == JTokenType.Null ? "None" : edited.ToString());
                return true;
            case StrPropertyData s:
                s.Value = edited.Type == JTokenType.Null ? null : new FString(edited.ToString());
                return true;
            case Utf8StrPropertyData u8:
                u8.Value = edited.Type == JTokenType.Null ? null : new FString(edited.ToString());
                return true;
            case TextPropertyData t:
                PatchText(t, original as JObject, edited, path);
                return true;
            case SoftObjectPropertyData so:
                so.Value = SoftPath(edited);
                return true;
            case SoftObjectPathPropertyData sp:
                sp.Value = SoftPath(edited);
                return true;
            case ObjectPropertyData obj:
                if (!_references.TryResolve(edited, out var index, out var error)) throw new FormatException(error);
                obj.Value = index;
                return true;
            case GameplayTagContainerPropertyData tags:
                tags.Value = AsArray(edited).Select(t => Name(t is JObject tagObj ? tagObj["TagName"]?.ToString() : t.ToString())).ToArray();
                return true;
            case SetPropertyData set:
                PatchArray(set, original as JArray, AsArray(edited), path);
                return false;
            case ArrayPropertyData array:
                PatchArray(array, original as JArray, AsArray(edited), path);
                return false;
            case MapPropertyData map:
                PatchMap(map, original as JArray, AsArray(edited), path);
                return false;
            case StructPropertyData st:
                return PatchStruct(st, original, edited, path);
            default:
                return SetNative(property, original, edited, path);
        }
    }

    private bool PatchStruct(StructPropertyData st, JToken original, JToken edited, string path)
    {
        st.Value ??= [];

        // natively serialized structs (Vector, Guid, GameplayTagContainer...) are a single typed property inside the struct
        if (st.Value.Count == 1 && st.Value[0].HasCustomStructSerialization)
            return SetValueCore(st.Value[0], original, edited, path);

        if (edited is not JObject editedObj)
        {
            if (st.Value.Count == 0 && original == null)
                throw new FormatException($"構造体 {st.StructType} はオブジェクトで指定してください");
            throw new FormatException("構造体はオブジェクトで指定してください");
        }

        PatchContainer(st.Value, original as JObject ?? [], editedObj, path, st.StructType?.ToString());
        return false;
    }

    private void PatchArray(ArrayPropertyData array, JArray original, JArray edited, string path)
    {
        original ??= [];
        var values = (array.Value ?? []).ToList();
        var common = Math.Min(values.Count, edited.Count);
        for (var i = 0; i < common; i++)
        {
            var o = i < original.Count ? original[i] : null;
            if (JToken.DeepEquals(o, edited[i])) continue;
            SetValue(values[i], o, edited[i], $"{path}[{i}]");
        }

        if (edited.Count > values.Count)
        {
            _report.Change(path, $"要素数 {values.Count} → {edited.Count}");
            for (var i = values.Count; i < edited.Count; i++)
            {
                var (element, templateJson) = NewElement(array, values, original);
                if (element == null)
                {
                    _report.Error($"{path}[{i}]", "空の配列に要素の型が分からないため追加できません");
                    break;
                }
                if (!SetValue(element, templateJson, edited[i], $"{path}[{i}]")) break;
                values.Add(element);
            }
        }
        else if (edited.Count < values.Count)
        {
            _report.Change(path, $"要素数 {values.Count} → {edited.Count}");
            values.RemoveRange(edited.Count, values.Count - edited.Count);
        }

        array.Value = values.ToArray();
    }

    private (PropertyData element, JToken templateJson) NewElement(ArrayPropertyData array, List<PropertyData> values, JArray original)
    {
        if (values.Count > 0)
            return (DeepClone(values[^1]), original.Count >= values.Count ? original[values.Count - 1] : null);

        if (array.DummyStruct != null)
        {
            var dummy = (StructPropertyData) DeepClone(array.DummyStruct);
            dummy.Value = [];
            return (dummy, null);
        }

        if (array.ArrayType == null) return (null, null);
        var created = MainSerializer.TypeToClass(array.ArrayType, array.Name, array.Ancestry, array.Name, null, _asset);
        if (created is StructPropertyData { StructType: null } createdStruct &&
            _asset.Mappings?.TryGetPropertyData(array.Name, array.Ancestry, _asset, out UsmapArrayData arrayData) == true &&
            arrayData.InnerType is UsmapStructData structData)
        {
            createdStruct.StructType = new FName(_asset, structData.StructType);
            createdStruct.Value = [];
        }
        return (created, null);
    }

    private void PatchMap(MapPropertyData map, JArray original, JArray edited, string path)
    {
        original ??= [];
        var entries = map.Value.ToList();
        var result = new TMap<PropertyData, PropertyData>();
        for (var i = 0; i < edited.Count; i++)
        {
            var entryPath = $"{path}[{i}]";
            if (edited[i] is not JObject editedEntry)
            {
                _report.Error(entryPath, "Map の要素は {\"Key\": ..., \"Value\": ...} で指定してください");
                return;
            }

            PropertyData key, value;
            JToken originalKey = null, originalValue = null;
            if (i < entries.Count)
            {
                (key, value) = (entries[i].Key, entries[i].Value);
                if (i < original.Count && original[i] is JObject originalEntry)
                {
                    originalKey = originalEntry["Key"];
                    originalValue = originalEntry["Value"];
                }
            }
            else if (entries.Count > 0)
            {
                key = DeepClone(entries[^1].Key);
                value = DeepClone(entries[^1].Value);
                if (original.Count >= entries.Count && original[entries.Count - 1] is JObject templateEntry)
                {
                    originalKey = templateEntry["Key"];
                    originalValue = templateEntry["Value"];
                }
            }
            else
            {
                _report.Error(entryPath, "空の Map には要素の型が分からないため追加できません");
                return;
            }

            if (!JToken.DeepEquals(originalKey, editedEntry["Key"]) && !SetValue(key, originalKey, editedEntry["Key"], $"{entryPath}.Key")) return;
            if (!JToken.DeepEquals(originalValue, editedEntry["Value"]) && !SetValue(value, originalValue, editedEntry["Value"], $"{entryPath}.Value")) return;

            if (result.ContainsKey(key))
            {
                _report.Error(entryPath, "Map のキーが重複しています");
                return;
            }
            result.Add(key, value);
        }

        if (edited.Count != entries.Count) _report.Change(path, $"要素数 {entries.Count} → {edited.Count}");
        map.Value = result;
    }

    private void PatchText(TextPropertyData text, JObject original, JToken edited, string path)
    {
        if (edited is not JObject e)
            throw new FormatException("テキストは {\"Namespace\", \"Key\", \"SourceString\"} 形式で指定してください");

        original ??= [];
        bool Changed(string field) => !JToken.DeepEquals(original[field], e[field]);

        switch (text.HistoryType)
        {
            case TextHistoryType.Base:
            {
                if (Changed("Namespace")) text.Namespace = new FString(e["Namespace"]?.ToString() ?? string.Empty);
                if (Changed("Key")) text.Value = new FString(e["Key"]?.ToString() ?? string.Empty);
                if (Changed("SourceString"))
                {
                    text.CultureInvariantString = new FString(e["SourceString"]?.ToString() ?? string.Empty);
                }
                else if (Changed("LocalizedString"))
                {
                    // LocalizedString is not stored in the asset, it's what the localization resolved for the key
                    text.CultureInvariantString = new FString(e["LocalizedString"]?.ToString() ?? string.Empty);
                    _report.Warn(path, "LocalizedString は uasset に保存されないため SourceString に書き込みました。翻訳済みの言語ではキーに対応する翻訳が優先されます");
                }
                break;
            }
            case TextHistoryType.None:
                text.CultureInvariantString = e["CultureInvariantString"] is { Type: not JTokenType.Null } invariant ? new FString(invariant.ToString()) : null;
                break;
            case TextHistoryType.StringTableEntry:
                if (Changed("TableId")) text.TableId = Name(e["TableId"]?.ToString() ?? "None");
                if (Changed("Key")) text.Value = new FString(e["Key"]?.ToString() ?? string.Empty);
                break;
            case TextHistoryType.RawText:
                text.Value = new FString(e["SourceString"]?.ToString() ?? e["CultureInvariantString"]?.ToString() ?? string.Empty);
                break;
            default:
                throw new FormatException($"テキスト履歴 {text.HistoryType} の書き換えには対応していません");
        }
    }

    /// <summary>
    /// natively serialized structs: copies matching json members onto the value through reflection
    /// </summary>
    private bool SetNative(PropertyData property, JToken original, JToken edited, string path)
    {
        if (property is ColorPropertyData color)
        {
            if (edited is not JObject c) throw new FormatException("色は {\"R\", \"G\", \"B\", \"A\"} で指定してください");
            var hex = c["Hex"]?.ToString();
            if (original is JObject oc && JToken.DeepEquals(oc["R"], c["R"]) && JToken.DeepEquals(oc["G"], c["G"]) &&
                JToken.DeepEquals(oc["B"], c["B"]) && JToken.DeepEquals(oc["A"], c["A"]) && !string.IsNullOrEmpty(hex))
            {
                var argb = uint.Parse(hex.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (hex.TrimStart('#').Length <= 6) argb |= 0xFF000000;
                color.Value = System.Drawing.Color.FromArgb(unchecked((int) argb));
            }
            else
            {
                color.Value = System.Drawing.Color.FromArgb(
                    c["A"]?.Value<int>() ?? color.Value.A, c["R"]?.Value<int>() ?? color.Value.R,
                    c["G"]?.Value<int>() ?? color.Value.G, c["B"]?.Value<int>() ?? color.Value.B);
            }
            return true;
        }

        if (property is GuidPropertyData guid)
        {
            guid.Value = ParseGuid(edited.ToString());
            return true;
        }

        var valueMember = (MemberInfo) property.GetType().GetField("Value", BindingFlags.Public | BindingFlags.Instance)
                          ?? property.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);

        // some structs keep part of their members on the property itself (material inputs: Expression, OutputIndex... + Value as "Constant")
        if (edited is JObject editedObj && OwnMembers(property.GetType()).Any())
        {
            foreach (var (key, value) in editedObj)
            {
                var oldValue = (original as JObject)?[key];
                if (JToken.DeepEquals(oldValue, value)) continue;

                var member = OwnMembers(property.GetType()).FirstOrDefault(m => m.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (member == null && key.Equals("Constant", StringComparison.OrdinalIgnoreCase)) member = valueMember;
                if (member != null)
                {
                    SetMember(member, property, Assign(GetMember(member, property), MemberType(member), oldValue, value));
                    continue;
                }

                // not one of the property's own fields, then it's a member of the value
                if (valueMember == null) throw new FormatException($"{property.GetType().Name} にメンバー '{key}' が無いため書き換えられません");
                SetMember(valueMember, property, Assign(GetMember(valueMember, property), MemberType(valueMember),
                    new JObject { [key] = oldValue }, new JObject { [key] = value }));
            }
            return true;
        }

        if (valueMember == null) throw new FormatException("この型の書き換えには対応していません");
        SetMember(valueMember, property, Assign(GetMember(valueMember, property), MemberType(valueMember), original, edited));
        return true;
    }

    /// <summary>
    /// public members a PropertyData subclass declares on top of the base PropertyData/PropertyData&lt;T&gt;
    /// </summary>
    private static IEnumerable<MemberInfo> OwnMembers(Type type)
    {
        for (var t = type; t != null && t != typeof(PropertyData) && !(t.IsGenericType && t.GetGenericTypeDefinition() == typeof(PropertyData<>)); t = t.BaseType)
        {
            foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                yield return field;
        }
    }

    private object Assign(object current, Type type, JToken original, JToken edited)
    {
        if (typeof(PropertyData).IsAssignableFrom(type) && current is PropertyData inner)
        {
            SetValueCore(inner, original, edited, "");
            return inner;
        }
        if (type == typeof(FPackageIndex))
        {
            if (!_references.TryResolve(edited, out var index, out var error)) throw new FormatException(error);
            return index;
        }
        if (type == typeof(FName)) return Name(edited.ToString());
        if (type == typeof(FString)) return edited.Type == JTokenType.Null ? null : new FString(edited.ToString());
        if (type == typeof(Guid)) return ParseGuid(edited.ToString());
        if (type == typeof(bool)) return edited.Value<bool>();
        if (type.IsEnum)
        {
            var member = edited.ToString().Split("::")[^1];
            return Enum.TryParse(type, member, true, out var parsed)
                ? parsed
                : throw new FormatException($"{type.Name} に '{member}' はありません（候補: {string.Join(", ", Enum.GetNames(type))}）");
        }
        if (type.IsPrimitive || type == typeof(decimal))
            return type == typeof(ulong)
                ? ulong.Parse(edited.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture) // may exceed long, read as BigInteger
                : Convert.ChangeType(((JValue) edited).Value, type, CultureInfo.InvariantCulture);

        if (edited is not JObject obj) throw new FormatException($"{type.Name} はオブジェクトで指定してください");

        // IntPoint is an int[2], per-platform values keep the default first
        if (type.IsArray && current is Array currentArray)
        {
            var array = (Array) currentArray.Clone();
            foreach (var (key, value) in obj)
            {
                var oldValue = (original as JObject)?[key];
                if (JToken.DeepEquals(oldValue, value)) continue;

                var index = key.ToUpperInvariant() switch
                {
                    "X" or "DEFAULT" or "VALUE" => 0,
                    "Y" => 1,
                    "Z" => 2,
                    "W" => 3,
                    _ => -1
                };
                if (index < 0 || index >= array.Length) throw new FormatException($"'{key}' は書き換えられません");
                array.SetValue(Assign(array.GetValue(index), type.GetElementType()!, oldValue, value), index);
            }
            return array;
        }

        var boxed = current ?? Activator.CreateInstance(type);
        foreach (var (key, value) in obj)
        {
            var oldValue = (original as JObject)?[key];
            if (JToken.DeepEquals(oldValue, value)) continue;

            var member = (MemberInfo) type.GetProperty(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                         ?? type.GetField(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (member == null || member is PropertyInfo { CanWrite: false })
                throw new FormatException($"{type.Name} にメンバー '{key}' が無いため書き換えられません");

            SetMember(member, boxed, Assign(GetMember(member, boxed), MemberType(member), oldValue, value));
        }
        return boxed;
    }

    #endregion

    #region helpers

    private PropertyData CreateProperty(string name, int arrayIndex, string schemaName, string path)
    {
        if (_asset.Mappings == null)
        {
            _report.Error(path, "このプロパティは元のアセットに存在しません。追加するにはマッピング(.usmap)が必要です");
            return null;
        }

        var ancestry = new AncestryInfo();
        ancestry.SetAsParent(new FName(_asset, schemaName ?? string.Empty));
        var propertyName = new FName(_asset, name);
        if (!_asset.Mappings.TryGetPropertyData(propertyName, ancestry, _asset, out UsmapPropertyData data))
        {
            _report.Error(path, $"'{schemaName}' に '{name}' というプロパティはありません（綴りを確認してください）");
            return null;
        }

        var property = Create(data, propertyName, ancestry, schemaName);
        property.ArrayIndex = arrayIndex;
        return property;
    }

    private PropertyData Create(UsmapPropertyData data, FName name, AncestryInfo ancestry, string parentName)
    {
        var property = MainSerializer.TypeToClass(new FName(_asset, data.Type.ToString()), name, ancestry, new FName(_asset, parentName ?? string.Empty), null, _asset);
        switch (property)
        {
            case StructPropertyData st when data is UsmapStructData structData:
                st.StructType = new FName(_asset, structData.StructType);
                st.Value = [];
                if (NativeStruct(structData.StructType, name, ancestry, parentName) is { } inner) st.Value.Add(inner);
                break;
            case EnumPropertyData en when data is UsmapEnumData enumData:
                en.EnumType = new FName(_asset, enumData.Name);
                en.InnerType = new FName(_asset, enumData.InnerType.Type.ToString());
                break;
            case BytePropertyData by when data is UsmapEnumData byteEnum:
                by.EnumType = new FName(_asset, byteEnum.Name);
                by.ByteType = BytePropertyType.FName;
                break;
            case ArrayPropertyData array when data is UsmapArrayData arrayData:
                array.ArrayType = new FName(_asset, arrayData.InnerType.Type.ToString());
                array.Value = [];
                if (arrayData.InnerType is UsmapStructData innerStruct)
                {
                    array.DummyStruct = new StructPropertyData(name, new FName(_asset, innerStruct.StructType)) { Value = [] };
                }
                break;
            case MapPropertyData map when data is UsmapMapData mapData:
                map.KeyType = new FName(_asset, mapData.InnerType.Type.ToString());
                map.ValueType = new FName(_asset, mapData.ValueType.Type.ToString());
                map.Value = new TMap<PropertyData, PropertyData>();
                break;
            case TextPropertyData text:
                text.HistoryType = TextHistoryType.Base;
                text.Namespace = new FString(string.Empty);
                text.Value = new FString(Guid.NewGuid().ToString("N").ToUpperInvariant());
                text.CultureInvariantString = new FString(string.Empty);
                break;
            case ObjectPropertyData obj:
                obj.Value = FPackageIndex.FromRawIndex(0);
                break;
        }
        return property;
    }

    /// <summary>
    /// UAssetAPI registers natively serialized structs (Vector, Guid...) as property types of their own
    /// </summary>
    private PropertyData NativeStruct(string structType, FName name, AncestryInfo ancestry, string parentName)
    {
        try
        {
            var inner = MainSerializer.TypeToClass(new FName(_asset, structType), name, ancestry, new FName(_asset, parentName ?? string.Empty), null, _asset);
            return inner is { HasCustomStructSerialization: true } and not StructPropertyData ? inner : null;
        }
        catch (FormatException)
        {
            return null; // not a registered type, serialized as a regular property list
        }
    }

    private FName Name(string value) => FName.FromString(_asset, value ?? "None");

    private FName EnumName(FName enumType, string value, FName current)
    {
        var bare = value.Contains("::") ? value[(value.LastIndexOf("::", StringComparison.Ordinal) + 2)..] : value;
        var full = value.Contains("::") ? value : enumType != null ? $"{enumType}::{value}" : value;

        // unversioned enums are written by index, so the name must be one of the mapping's
        if (_asset.HasUnversionedProperties && enumType != null && _asset.Mappings?.EnumMap.TryGetValue(enumType.ToString(), out var mapped) == true)
        {
            if (mapped.Values.Values.Contains(full)) return FName.DefineDummy(_asset, full);
            if (mapped.Values.Values.Contains(bare)) return FName.DefineDummy(_asset, bare);
            throw new FormatException($"列挙型 {enumType} に '{bare}' はありません（候補: {string.Join(", ", mapped.Values.Values.Take(12))}{(mapped.Values.Count > 12 ? " ..." : "")}）");
        }

        // tagged properties store the name itself, keep whichever form the asset used
        var currentText = current?.ToString();
        return Name(currentText == null || currentText.Contains("::") ? full : bare);
    }

    private FSoftObjectPath SoftPath(JToken edited)
    {
        if (edited == null || edited.Type == JTokenType.Null) return new FSoftObjectPath(null, null, null);

        string assetPath, subPath;
        if (edited is JObject obj)
        {
            assetPath = obj["AssetPathName"]?.ToString() ?? string.Empty;
            subPath = obj["SubPathString"]?.ToString() ?? string.Empty;
        }
        else
        {
            assetPath = edited.ToString();
            subPath = string.Empty;
        }

        FSoftObjectPath result;
        if (_asset.ObjectVersionUE5 >= ObjectVersionUE5.FSOFTOBJECTPATH_REMOVE_ASSET_PATH_FNAMES)
        {
            var dot = assetPath.LastIndexOf('.');
            var packageName = dot > 0 ? assetPath[..dot] : assetPath;
            var assetName = dot > 0 ? assetPath[(dot + 1)..] : string.Empty;
            result = new FSoftObjectPath(Name(string.IsNullOrEmpty(packageName) ? "None" : packageName), Name(string.IsNullOrEmpty(assetName) ? "None" : assetName), new FString(subPath));
        }
        else
        {
            result = new FSoftObjectPath(null, Name(string.IsNullOrEmpty(assetPath) ? "None" : assetPath), new FString(subPath));
        }

        // packages saved with a soft object path table reference entries by index
        if (_asset.SoftObjectPathList is { Count: > 0 } list && !list.Contains(result)) list.Add(result);
        return result;
    }

    private static Guid ParseGuid(string value)
    {
        var hex = value.Replace("-", "").Replace("{", "").Replace("}", "");
        if (hex.Length != 32) throw new FormatException("GUID は 32 桁の16進数で指定してください");

        // FModel prints the four uint32 the engine stores, in order
        var bytes = new byte[16];
        for (var i = 0; i < 4; i++)
        {
            var part = uint.Parse(hex.Substring(i * 8, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            BitConverter.GetBytes(part).CopyTo(bytes, i * 4);
        }
        return new Guid(bytes);
    }

    private static long Integer(JToken token) => token.Type switch
    {
        JTokenType.Integer => token.Value<long>(),
        JTokenType.Float when Math.Abs(token.Value<double>() % 1) < double.Epsilon => (long) token.Value<double>(),
        JTokenType.String when long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => throw new FormatException($"整数で指定してください（入力: {token}）")
    };

    private static double Number(JToken token) => token.Type switch
    {
        JTokenType.Integer or JTokenType.Float => token.Value<double>(),
        JTokenType.String when double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => throw new FormatException($"数値で指定してください（入力: {token}）")
    };

    private static JArray AsArray(JToken token) => token switch
    {
        JArray array => array,
        null or { Type: JTokenType.Null } => [],
        _ => throw new FormatException("配列で指定してください")
    };

    private static IEnumerable<string> Keys(JObject a, JObject b)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in a.Properties()) if (seen.Add(p.Name)) yield return p.Name;
        foreach (var p in b.Properties()) if (seen.Add(p.Name)) yield return p.Name;
    }

    private static (string name, int arrayIndex) ParseKey(string key)
    {
        if (key.EndsWith(']'))
        {
            var open = key.LastIndexOf('[');
            if (open > 0 && int.TryParse(key[(open + 1)..^1], out var index))
                return (key[..open], index);
        }
        return (key, 0);
    }

    private static string Describe(JToken token)
    {
        var text = token?.Type is JTokenType.Object or JTokenType.Array
            ? token.ToString(Newtonsoft.Json.Formatting.None)
            : token?.ToString() ?? "null";
        return text.Length > 80 ? text[..77] + "..." : text;
    }

    internal static PropertyData DeepClone(PropertyData property)
    {
        var clone = (PropertyData) property.Clone();
        switch (clone)
        {
            case StructPropertyData st when st.Value != null:
                st.Value = st.Value.Select(DeepClone).ToList();
                break;
            case ArrayPropertyData array when array.Value != null:
                array.Value = array.Value.Select(DeepClone).ToArray();
                break;
            case MapPropertyData map when map.Value != null:
            {
                var copy = new TMap<PropertyData, PropertyData>();
                foreach (var entry in map.Value) copy.Add(DeepClone(entry.Key), DeepClone(entry.Value));
                map.Value = copy;
                break;
            }
        }
        return clone;
    }

    private static object GetMember(MemberInfo member, object target) => member switch
    {
        FieldInfo field => field.GetValue(target),
        PropertyInfo property => property.GetValue(target),
        _ => null
    };

    private static void SetMember(MemberInfo member, object target, object value)
    {
        switch (member)
        {
            case FieldInfo field:
                field.SetValue(target, value);
                break;
            case PropertyInfo property:
                property.SetValue(target, value);
                break;
        }
    }

    private static Type MemberType(MemberInfo member) => member switch
    {
        FieldInfo field => field.FieldType,
        PropertyInfo property => property.PropertyType,
        _ => typeof(object)
    };

    #endregion
}
