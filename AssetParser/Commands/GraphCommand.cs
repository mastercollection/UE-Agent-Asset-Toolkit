using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Reflection;
using System.Threading.Tasks;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.CustomVersions;
using AssetParser.Core;
using AssetParser.Commands;
using AssetParser.Parsers;
using static AssetParser.Core.Helpers;
using static AssetParser.Core.AssetTypeDetector;
using static AssetParser.Core.AssetRefHelper;
using static AssetParser.Parsers.ControlFlowAnalyzer;
using static AssetParser.Parsers.BytecodeAnalyzer;
using static AssetParser.Commands.SummaryCommand;
using static AssetParser.Commands.InspectCommand;
using static AssetParser.Commands.WidgetCommand;
using static AssetParser.Commands.DataTableCommand;
using static AssetParser.Commands.BlueprintCommand;
using static AssetParser.Commands.GraphCommand;
using static AssetParser.Commands.BytecodeCommand;
using static AssetParser.Commands.MaterialCommand;
using static AssetParser.Commands.MaterialFunctionCommand;
using static AssetParser.Commands.ReferencesCommand;
using static AssetParser.Commands.BatchCommands;
using static AssetParser.Commands.BatchBlueprintCommand;
using static AssetParser.Commands.BatchWidgetCommand;
using static AssetParser.Commands.BatchMaterialCommand;
using static AssetParser.Commands.BatchDataTableCommand;

namespace AssetParser.Commands
{
    public static class GraphCommand
    {
        
        // --- Pin binary reader helpers ---
        
        public static string ReadFNameStr(BinaryReader r, IReadOnlyList<FString> nameMap)
        {
            int idx = r.ReadInt32();
            int num = r.ReadInt32();
            if (idx < 0 || idx >= nameMap.Count) return $"[idx:{idx}]";
            string name = nameMap[idx].ToString();
            if (num > 0) name += $"_{num - 1}";
            return name;
        }
        
        public static string ReadFString(BinaryReader r)
        {
            int len = r.ReadInt32();
            if (len == 0) return "";
            if (len > 0)
            {
                var bytes = r.ReadBytes(len);
                return System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            }
            else
            {
                int charCount = -len;
                var bytes = r.ReadBytes(charCount * 2);
                return System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0');
            }
        }
        
        // Read FText: uint32 Flags + int8 HistoryType + type-specific data
        // Source: Text.cpp FText::SerializeText, TextHistory.cpp for each type
        // Supported types: -1(None), 0(Base), 1(NamedFormat), 2(OrderedFormat),
        //   3(ArgumentFormat), 10(Transform), 11(StringTableEntry)
        // NOT yet supported: 4(AsDateTime), 5(AsDate), 6(AsTime), 7(AsNumber),
        //   8(AsCurrency), 9(AsPercent) — these require reading recursive FText +
        //   format options. Add them here if you hit "Unsupported FText HistoryType N".
        public static string ReadFText(BinaryReader r)
        {
            uint flags = r.ReadUInt32();
            sbyte historyType = r.ReadSByte();
        
            switch (historyType)
            {
                case -1: // None
                {
                    // bool bHasCultureInvariantString (serialized as uint32)
                    uint hasCultureInvariant = r.ReadUInt32();
                    if (hasCultureInvariant != 0)
                    {
                        return ReadFString(r);
                    }
                    return "";
                }
                case 0: // Base
                {
                    string ns = ReadFString(r);    // Namespace
                    string key = ReadFString(r);   // Key
                    string src = ReadFString(r);   // SourceString
                    return src;
                }
                case 1: // NamedFormat
                case 2: // OrderedFormat
                case 3: // ArgumentFormat
                {
                    // FormatText (recursive FText)
                    string fmtText = ReadFText(r);
                    // Arguments: TMap<FString, FFormatArgumentValue>
                    int argCount = r.ReadInt32();
                    for (int a = 0; a < argCount; a++)
                    {
                        ReadFString(r); // key
                        ReadFormatArgumentValue(r);
                    }
                    return fmtText;
                }
                case 10: // Transform
                {
                    ReadFText(r);   // SourceText
                    r.ReadByte();   // TransformType (uint8)
                    return "";
                }
                case 11: // StringTableEntry
                {
                    string tableId = ReadFString(r);
                    string key = ReadFString(r);
                    return $"[ST:{tableId}/{key}]";
                }
                default:
                {
                    throw new FormatException($"Unsupported FText HistoryType {historyType} at position {r.BaseStream.Position}");
                }
            }
        }
        
        // Read FFormatArgumentValue: int8 TypeIndex + type-specific data
        public static void ReadFormatArgumentValue(BinaryReader r)
        {
            sbyte typeIdx = r.ReadSByte();
            switch (typeIdx)
            {
                case 0: r.ReadInt64(); break;   // Int
                case 1: r.ReadUInt64(); break;  // UInt
                case 2: r.ReadSingle(); break;  // Float
                case 3: r.ReadDouble(); break;  // Double
                case 4: ReadFText(r); break;    // Text (recursive)
                case 5: r.ReadSByte(); break;   // Gender (ETextGender)
                default: throw new FormatException($"Unknown FFormatArgumentValue type {typeIdx}");
            }
        }
        
        public static Guid ReadFGuid(BinaryReader r)
        {
            return new Guid(r.ReadBytes(16));
        }
        
        // Read a pin reference (from LinkedTo, SubPins, ParentPin, RefPassThrough)
        // Returns (owningNodeExportIndex, pinGuid) or null if null ref
        public static (int nodeExportIndex, Guid pinGuid)? ReadPinRef(BinaryReader r)
        {
            uint isNull = r.ReadUInt32();
            if (isNull != 0) return null;
            int nodeRef = r.ReadInt32(); // FPackageIndex: positive = export index
            var pinGuid = ReadFGuid(r);
            return (nodeRef, pinGuid);
        }
        
        // Read FEdGraphTerminalType (for Map value types — only present when ContainerType == Map)
        // Source: EdGraphPin.cpp FEdGraphTerminalType::Serialize
        // Returns (TerminalCategory, TerminalSubCategoryObject package index) so a map's value type can
        // be carried into the IR (the key type is the pin's own Category; the value type lives here).
        public static (string category, int subObjIdx) ReadTerminalType(BinaryReader r, IReadOnlyList<FString> nameMap)
        {
            string cat = ReadFNameStr(r, nameMap);  // TerminalCategory
            ReadFNameStr(r, nameMap);  // TerminalSubCategory
            int subObj = r.ReadInt32();             // TerminalSubCategoryObject (UObject*)
            r.ReadUInt32();            // bTerminalIsConst (bool as uint32)
            r.ReadUInt32();            // bTerminalIsWeakPointer (bool as uint32)
            r.ReadUInt32();            // bTerminalIsUObjectWrapper (UE5+ only, bool as uint32)
            return (cat, subObj);
        }
        
        // Read FSimpleMemberReference
        public static void ReadSimpleMemberRef(BinaryReader r, IReadOnlyList<FString> nameMap)
        {
            r.ReadInt32();             // MemberParent (UObject*)
            ReadFNameStr(r, nameMap);  // MemberName (FName)
            ReadFGuid(r);              // MemberGuid (FGuid)
        }
        
        // Reads one pin from the binary Extras blob of a K2Node export.
        // Format derived from UE 5.7 source: EdGraphPin.cpp (Pin::Serialize, FEdGraphPinType::Serialize)
        // and EdGraphNode.cpp (UEdGraphNode::SerializeAsOwningNode).
        //
        // VERSION SENSITIVITY: This assumes editor-saved (WITH_EDITOR) assets. Cooked/packaged
        // builds omit PinFriendlyName, PersistentGuid, BitField, and bSerializeAsSinglePrecisionFloat.
        // If adapting for cooked assets, skip those fields.
        //
        // UE VERSION NOTES (fields that vary by engine version):
        //   - bSerializeAsSinglePrecisionFloat: Added ~5.4-5.7 behind
        //     FUE5ReleaseStreamObjectVersion::SerializeFloatPinDefaultValuesAsSinglePrecision.
        //     If pins fail at "PinType.bSerializeAsSinglePrecisionFloat", remove that ReadUInt32().
        //   - bTerminalIsUObjectWrapper (in ReadTerminalType): UE5+ only.
        //   - SourceIndex: Conditional in source (only serialized when >=0), but appears always
        //     present in editor assets we've tested (5.5, 5.7).
        //
        // DEBUGGING: If pin parsing fails, the exception includes the field name, pin name, and
        // stream position. Compare stream position against a hex dump of the Extras blob to find
        // where the format diverges.
        public static ParsedPin ReadOnePin(BinaryReader r, UAsset asset, IReadOnlyList<FString> nameMap)
        {
            var pin = new ParsedPin();
            pin.LinkedTo = new List<(int, Guid)>();
            long pinStart = r.BaseStream.Position;
            string lastField = "start";
        
            try
            {
                // --- From SerializePin wrapper (EdGraphNode.cpp: SerializeAsOwningNode) ---
                lastField = "bNullPtr";
                uint bNullPtr = r.ReadUInt32();
                if (bNullPtr != 0) throw new FormatException($"Unexpected null pin in owning array (bNullPtr={bNullPtr})");
        
                lastField = "SerializePin.OwningNode";
                r.ReadInt32();
        
                lastField = "SerializePin.PinGuid";
                ReadFGuid(r);
        
                // --- From Pin->Serialize ---
                lastField = "Serialize.OwningNode";
                r.ReadInt32();
        
                lastField = "Serialize.PinId";
                pin.PinId = ReadFGuid(r);
        
                lastField = "PinName";
                pin.Name = ReadFNameStr(r, nameMap);
        
                lastField = "PinFriendlyName";
                ReadFText(r); // WITH_EDITOR only — omitted in cooked builds
        
                lastField = "SourceIndex";
                r.ReadInt32();
        
                lastField = "PinToolTip";
                ReadFString(r);
        
                lastField = "Direction";
                byte dir = r.ReadByte();
                pin.Direction = dir == 0 ? "in" : "out";
        
                // --- FEdGraphPinType ---
                lastField = "PinType.PinCategory";
                pin.Category = ReadFNameStr(r, nameMap);
        
                lastField = "PinType.PinSubCategory";
                pin.SubCategory = ReadFNameStr(r, nameMap);
        
                lastField = "PinType.PinSubCategoryObject";
                int subCatObj = r.ReadInt32();
                // Full object path ("/Script/Engine.Actor", "/Game/.../AC_Mob.AC_Mob_C") so the
                // materializer can LoadObject project-defined pin/variable types that are not yet
                // loaded; a bare short name silently falls back to UObject and breaks type-checks.
                pin.SubCategoryObject = subCatObj != 0
                    ? SerializeObjectRef(asset, new FPackageIndex(subCatObj))
                    : "";
        
                lastField = "PinType.ContainerType";
                pin.ContainerType = r.ReadByte(); // EPinContainerType: 0=None, 1=Array, 2=Set, 3=Map
                if (pin.ContainerType == 3) // Map: read PinValueType (FEdGraphTerminalType)
                {
                    lastField = "PinType.PinValueType";
                    var (vcat, vsub) = ReadTerminalType(r, nameMap);
                    pin.ValueCategory = vcat;
                    pin.ValueSubObject = vsub != 0 ? SerializeObjectRef(asset, new FPackageIndex(vsub)) : "";
                }
        
                lastField = "PinType.bIsReference";
                pin.IsReference = r.ReadUInt32() != 0;

                lastField = "PinType.bIsWeakPointer";
                r.ReadUInt32();

                lastField = "PinType.MemberRef";
                ReadSimpleMemberRef(r, nameMap);

                lastField = "PinType.bIsConst";
                pin.IsConst = r.ReadUInt32() != 0;
        
                lastField = "PinType.bIsUObjectWrapper";
                r.ReadUInt32();
        
                // UE 5.4+: bSerializeAsSinglePrecisionFloat (WITH_EDITOR + custom-version-gated)
                // Source: EdGraphPin.cpp, gated by
                // FUE5ReleaseStreamObjectVersion::SerializeFloatPinDefaultValuesAsSinglePrecision
                if (asset.GetCustomVersion<FUE5ReleaseStreamObjectVersion>()
                    >= FUE5ReleaseStreamObjectVersion.SerializeFloatPinDefaultValuesAsSinglePrecision)
                {
                    lastField = "PinType.bSerializeAsSinglePrecisionFloat";
                    r.ReadUInt32();
                }
        
                // --- Values ---
                lastField = "DefaultValue";
                pin.DefaultValue = ReadFString(r);
        
                lastField = "AutogeneratedDefaultValue";
                pin.AutoDefault = ReadFString(r);
        
                lastField = "DefaultObject";
                int defObjIdx = r.ReadInt32();
                // Full object path ("/Game/.../Asset.Asset") so the materializer can LoadObject the literal
                // default (a sound/class/etc.); a bare short name would fail to reload.
                pin.DefaultObjectRef = defObjIdx != 0
                    ? SerializeObjectRef(asset, new FPackageIndex(defObjIdx))
                    : null;
        
                lastField = "DefaultTextValue";
                {
                    string textDefault = ReadFText(r);
                    pin.TextDefault = string.IsNullOrEmpty(textDefault) ? null : textDefault;
                }
        
                // --- LinkedTo array ---
                lastField = "LinkedTo.Count";
                int linkedCount = r.ReadInt32();
                for (int i = 0; i < linkedCount; i++)
                {
                    lastField = $"LinkedTo[{i}]";
                    var lref = ReadPinRef(r);
                    if (lref != null) pin.LinkedTo.Add(lref.Value);
                }
        
                // --- SubPins array ---
                lastField = "SubPins.Count";
                int subPinCount = r.ReadInt32();
                for (int i = 0; i < subPinCount; i++)
                {
                    lastField = $"SubPins[{i}]";
                    ReadPinRef(r);
                }
        
                lastField = "ParentPin";
                ReadPinRef(r);
        
                lastField = "RefPassThrough";
                ReadPinRef(r);
        
                // --- Editor-only tail (WITH_EDITOR — omitted in cooked builds) ---
                lastField = "PersistentGuid";
                ReadFGuid(r);
        
                lastField = "BitField";
                uint bitField = r.ReadUInt32(); // bHidden(0), bNotConnectable(1), bDefaultValueIsReadOnly(2), bDefaultValueIsIgnored(3), bAdvancedView(4), bOrphanedPin(5)
                pin.IsHidden = (bitField & (1 << 0)) != 0;
                pin.IsOrphaned = (bitField & (1 << 5)) != 0;
            }
            catch (Exception ex)
            {
                long failPos = r.BaseStream.Position;
                throw new FormatException(
                    $"Pin parse failed at field '{lastField}', pin '{pin.Name ?? "?"}', " +
                    $"stream pos {failPos}/{r.BaseStream.Length} (pin started at {pinStart}): {ex.Message}");
            }
        
            return pin;
        }
        
        // Serialize one template-archetype property delta to an ImportText-compatible string, for
        // round-tripping AddComponent component-template overrides (which live outside the graph in
        // UBlueprint::ComponentTemplates). Returns null for unsupported types — a skipped property is
        // invisible to both sides of the diff, so a dropped type can never fake a false match (it can
        // only surface as a loud diff!=0, never a silent loss). Conservative on purpose: only types
        // with a clean serialize/ImportText fixpoint (scalars, string/name, enum, object ref, struct
        // of those) are emitted.
        private static string FmtD(double x) => x.ToString("R", CultureInfo.InvariantCulture);
        private static string FmtF(float x) => x.ToString("R", CultureInfo.InvariantCulture);
        // Quote + escape a string for use inside a container/struct ImportText literal (where bare
        // strings with spaces/commas/parens would mis-parse). Top-level string properties stay bare.
        private static string QuoteStr(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        // nested=true when serializing an element/member inside an array/map/struct: string and name
        // leaves are then quoted so the value reparses unambiguously.
        private static string? SerializePropertyValue(UAsset asset, PropertyData prop, bool nested = false)
        {
            switch (prop)
            {
                case BoolPropertyData b: return ((bool)b.Value) ? "true" : "false";
                // Native math structs (FVector etc.) serialize as raw bytes, so UAssetAPI exposes them
                // as dedicated typed properties — not a StructPropertyData inner list. Emit each in the
                // engine's canonical ImportText form so ImportText_Direct reparses it.
                case VectorPropertyData v:
                    return $"(X={FmtD(v.Value.X)},Y={FmtD(v.Value.Y)},Z={FmtD(v.Value.Z)})";
                case RotatorPropertyData r:
                    return $"(Pitch={FmtD(r.Value.Pitch)},Yaw={FmtD(r.Value.Yaw)},Roll={FmtD(r.Value.Roll)})";
                case Vector2DPropertyData v2:
                    return $"(X={FmtD(v2.Value.X)},Y={FmtD(v2.Value.Y)})";
                // FSlateBrush.ImageSize and similar Slate fields use the deprecated float Vector2 type.
                case DeprecateSlateVector2DPropertyData dv2:
                    return $"(X={FmtF(dv2.Value.X)},Y={FmtF(dv2.Value.Y)})";
                case Vector4PropertyData v4:
                    return $"(X={FmtD(v4.Value.X)},Y={FmtD(v4.Value.Y)},Z={FmtD(v4.Value.Z)},W={FmtD(v4.Value.W)})";
                case QuatPropertyData q:
                    return $"(X={FmtD(q.Value.X)},Y={FmtD(q.Value.Y)},Z={FmtD(q.Value.Z)},W={FmtD(q.Value.W)})";
                case LinearColorPropertyData lc:
                    return $"(R={FmtF(lc.Value.R)},G={FmtF(lc.Value.G)},B={FmtF(lc.Value.B)},A={FmtF(lc.Value.A)})";
                case BytePropertyData by:
                    return (by.EnumValue != null && by.EnumValue.ToString() != "None")
                        ? by.EnumValue.ToString()
                        : by.Value.ToString(CultureInfo.InvariantCulture);
                case EnumPropertyData e: return e.Value?.ToString();
                case IntPropertyData i: return i.Value.ToString(CultureInfo.InvariantCulture);
                case Int64PropertyData i64: return i64.Value.ToString(CultureInfo.InvariantCulture);
                case Int8PropertyData i8: return i8.Value.ToString(CultureInfo.InvariantCulture);
                case Int16PropertyData i16: return i16.Value.ToString(CultureInfo.InvariantCulture);
                case FloatPropertyData f: return f.Value.ToString("R", CultureInfo.InvariantCulture);
                case DoublePropertyData d: return d.Value.ToString("R", CultureInfo.InvariantCulture);
                case StrPropertyData s: return nested ? QuoteStr(s.Value?.ToString()) : (s.Value?.ToString() ?? "");
                case NamePropertyData n: return nested ? QuoteStr(n.Value?.ToString()) : (n.Value?.ToString() ?? "");
                // FText (TextBlock.Text, Button labels, ...): the most common widget property. A Base
                // history round-trips as NSLOCTEXT(namespace, key, source); a culture-invariant literal as
                // INVTEXT. ImportText_Direct reparses both. Format histories (runtime-built text) are
                // skipped — they have no stable literal form.
                case TextPropertyData txt:
                {
                    string EscTxt(string val) => (val ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
                    if (txt.HistoryType == TextHistoryType.Base)
                    {
                        return $"NSLOCTEXT(\"{EscTxt(txt.Namespace?.Value)}\", \"{EscTxt(txt.Value?.Value)}\", \"{EscTxt(txt.CultureInvariantString?.Value)}\")";
                    }
                    if (txt.HistoryType == TextHistoryType.None)
                    {
                        // Culture-invariant text, or an EMPTY FText (null CultureInvariantString) — emit
                        // INVTEXT("") rather than null, which would collapse an enclosing struct/array
                        // (e.g. a TabInfo whose ButtonText is empty).
                        return $"INVTEXT(\"{EscTxt(txt.CultureInvariantString?.Value ?? "")}\")";
                    }
                    return null;
                }
                // A null object reference is "None" in ExportText (ImportText reads it back as null).
                // Returning null here would collapse an enclosing struct/array (e.g. a TabInfo whose
                // optional ActivateWidget/IconImage are unset) to <unserialized>.
                case ObjectPropertyData o: return SerializeObjectRef(asset, o.Value) ?? "None";
                // SoftObject (asset path that may be unloaded): the engine ImportText form is the path
                // string "/Game/Path/Pkg.Asset" (+ ":SubPath" when set).
                case SoftObjectPropertyData soft:
                {
                    var pkg = soft.Value.AssetPath.PackageName?.ToString();
                    var assetName = soft.Value.AssetPath.AssetName?.ToString();
                    if (string.IsNullOrEmpty(pkg) || pkg == "None") return "";
                    var path = string.IsNullOrEmpty(assetName) || assetName == "None" ? pkg : $"{pkg}.{assetName}";
                    var sub = soft.Value.SubPathString?.Value;
                    if (!string.IsNullOrEmpty(sub)) path += ":" + sub;
                    return nested ? QuoteStr(path) : path;
                }
                case StructPropertyData st:
                {
                    // Native math structs (Vector, Rotator, ...) come through as a StructPropertyData
                    // wrapping a single typed child with the SAME name — unwrap to the child's canonical
                    // form (e.g. "(X=2,Y=2,Z=2)") so ImportText_Direct reparses it onto the FVector.
                    if (st.Value.Count == 1 && st.Value[0].Name?.ToString() == st.Name?.ToString())
                        return SerializePropertyValue(asset, st.Value[0], nested);
                    // User struct: (Field=Val,Field=Val,...)
                    var inner = new List<string>();
                    foreach (var ip in st.Value)
                    {
                        // A RawStructPropertyData member is a nested struct UAssetAPI couldn't parse (e.g. an
                        // FInstancedPropertyBag with a dynamic/fixed layout). It has no ImportText form and is
                        // typically auto-derived on rebuild (e.g. a camera-asset parameter bag rebuilt from the
                        // referenced asset), so OMIT it — emitting the struct's parseable members (CameraAsset,
                        // ...) round-trips, whereas collapsing the whole struct loses them too. Symmetric across
                        // extraction, so the diff stays 0.
                        if (ip.GetType().Name == "RawStructPropertyData") continue;
                        var v = SerializePropertyValue(asset, ip, true);
                        if (v == null) return null; // any other unsupported member => whole struct unsupported
                        inner.Add($"{ip.Name}={v}");
                    }
                    return "(" + string.Join(",", inner) + ")";
                }
                // TArray / TSet: (Elem0,Elem1,...), each element in its own nested ImportText form.
                case ArrayPropertyData arr:
                {
                    var items = new List<string>();
                    foreach (var elem in arr.Value)
                    {
                        var v = SerializePropertyValue(asset, elem, true);
                        if (v == null) return null; // unsupported element type => whole array unsupported
                        items.Add(v);
                    }
                    return "(" + string.Join(",", items) + ")";
                }
                // TMap: ((Key0, Value0),(Key1, Value1),...) — the engine's map ImportText form.
                case MapPropertyData map:
                {
                    var pairs = new List<string>();
                    foreach (var kv in map.Value)
                    {
                        var k = SerializePropertyValue(asset, kv.Key, true);
                        var v = SerializePropertyValue(asset, kv.Value, true);
                        if (k == null || v == null) return null;
                        pairs.Add($"({k}, {v})");
                    }
                    return "(" + string.Join(",", pairs) + ")";
                }
                default: return null;
            }
        }

        // Resolve an ObjectProperty value to a full object path the engine's ImportText can load
        // ("/Game/Path/Asset.Asset" for assets, "/Script/Module.Class" for class refs).
        private static string? SerializeObjectRef(UAsset asset, FPackageIndex idx)
        {
            if (idx == null || idx.Index == 0) return null;
            var path = ResolveObjectRef(idx)?.ToString();
            if (string.IsNullOrEmpty(path)) return null;
            // Class-ref tuple "(, /Script/Mod.Class, )" -> inner path
            if (path.StartsWith("(,") && path.EndsWith(", )"))
                return path.Substring(2, path.Length - 5).Trim();
            string objName = idx.IsImport()
                ? (idx.ToImport(asset)?.ObjectName.ToString() ?? "")
                : ResolvePackageIndex(asset, idx);
            // Bare package path -> full object path "/Game/X/Asset.Asset"
            if (path.StartsWith("/") && !path.Contains(".") && !string.IsNullOrEmpty(objName))
                return $"{path}.{objName}";
            return path;
        }

        // Strip an Unreal enum prefix ("RCIM_Cubic" -> "Cubic", "RCTM_Auto" -> "Auto").
        private static string StripEnumPrefix(string s)
        {
            int us = s.IndexOf('_');
            return (us >= 0 && us < s.Length - 1) ? s.Substring(us + 1) : s;
        }

        private static GraphTimelineKey ToTimelineKey(FRichCurveKey k) => new GraphTimelineKey
        {
            T = k.Time, V = k.Value, At = k.ArriveTangent, Lt = k.LeaveTangent,
            Interp = StripEnumPrefix(k.InterpMode.ToString()),
            Tangent = StripEnumPrefix(k.TangentMode.ToString()),
        };

        // Read all FRichCurves from a curve export. propName="FloatCurve" yields 1 (UCurveFloat); the
        // static array "FloatCurves" yields N (UCurveVector=3, UCurveLinearColor=4) in serialized order.
        private static List<List<GraphTimelineKey>> ReadCurves(NormalExport curve, string propName)
        {
            var result = new List<List<GraphTimelineKey>>();
            if (curve.Data == null) return result;
            foreach (var p in curve.Data)
            {
                if (p.Name?.ToString() != propName || p is not StructPropertyData rc) continue;
                var keys = new List<GraphTimelineKey>();
                var keysProp = rc.Value?.FirstOrDefault(x => x.Name?.ToString() == "Keys") as ArrayPropertyData;
                if (keysProp?.Value != null)
                {
                    foreach (var k in keysProp.Value)
                    {
                        // Each key arrives as a StructPropertyData wrapping a single RichCurveKeyPropertyData
                        // (native-struct representation), or directly as RichCurveKeyPropertyData.
                        var rk = k as RichCurveKeyPropertyData
                            ?? (k as StructPropertyData)?.Value?.FirstOrDefault() as RichCurveKeyPropertyData;
                        if (rk != null) keys.Add(ToTimelineKey(rk.Value));
                    }
                }
                result.Add(keys);
            }
            return result;
        }

        public static GraphData BuildGraphData(UAsset asset)
        {
            var nameMap = asset.GetNameMapIndexList();

            // --- Node identity lookup table ---
            // Maps K2Node type → property names to check for a human-readable target label
            var nodeTargetProps = new Dictionary<string, string[]>
            {
                ["K2Node_CallFunction"] = new[] { "FunctionReference" },
                ["K2Node_VariableGet"] = new[] { "VariableReference" },
                ["K2Node_VariableSet"] = new[] { "VariableReference" },
                ["K2Node_DynamicCast"] = new[] { "TargetType" },
                // ClassDynamicCast ("Cast To <Class> (class)") inherits TargetType from DynamicCast.
                ["K2Node_ClassDynamicCast"] = new[] { "TargetType" },
                // Composite (collapsed graph): identity is its BoundGraph (a UEdGraph export emitted as a
                // separate function); the name links the node to that subgraph.
                ["K2Node_Composite"] = new[] { "BoundGraph" },
                ["K2Node_CustomEvent"] = new[] { "CustomFunctionName" },
                ["K2Node_MacroInstance"] = new[] { "MacroGraphReference" },
                ["K2Node_Event"] = new[] { "EventReference" },
                // K2Node_ComponentBoundEvent has a dedicated branch in ResolveNodeTarget (emits the
                // Comp|Delegate|OwnerClass|ComponentClass tuple incl. an SCS class lookup).
                ["K2Node_CallDelegate"] = new[] { "DelegateReference" },
                ["K2Node_CreateDelegate"] = new[] { "SelectedFunctionName" },
                // Operators: PromotableOperator stores the operation as an FName ("Add",
                // "Less", ...); CommutativeAssociativeBinaryOperator stores a FunctionReference
                // (like CallFunction). Without these the operator identity is lost on extraction.
                ["K2Node_PromotableOperator"] = new[] { "OperationName" },
                ["K2Node_CommutativeAssociativeBinaryOperator"] = new[] { "FunctionReference" },
                // Make/Break struct identify their struct via a StructType object ref ("Vector",
                // "S_CharacterViewer_Data"); CallArrayFunction is a CallFunction variant whose
                // identity is its FunctionReference's MemberName (Array_Get, Array_Add, ...).
                ["K2Node_MakeStruct"] = new[] { "StructType" },
                ["K2Node_BreakStruct"] = new[] { "StructType" },
                ["K2Node_CallArrayFunction"] = new[] { "FunctionReference" },
                // CallParentFunction is a CallFunction variant calling the parent's version of an
                // overridden function; its identity is the FunctionReference MemberName (resolved
                // against the blueprint's parent hierarchy on re-materialization).
                ["K2Node_CallParentFunction"] = new[] { "FunctionReference" },
                // CallMaterialParameterCollectionFunction is a CallFunction variant for material-
                // library calls (SetScalar/VectorParameterValue, CreateDynamicMaterialInstance, ...);
                // identity is the FunctionReference MemberName, resolved against KismetMaterialLibrary.
                ["K2Node_CallMaterialParameterCollectionFunction"] = new[] { "FunctionReference" },
            };

            string ResolveNodeTarget(NormalExport node, string nodeType)
            {
                // K2Node_Message (interface message call): the self pin is generic Object, so the
                // interface can't be recovered from pins. Encode the FunctionReference's owning
                // interface class and function name as "<InterfaceClass>:<Function>" so the message
                // can be re-resolved (mirrors the MacroInstance "<library>:<macro>" form).
                // K2Node_InputKey (key event node): the bound key lives in the FKey "InputKey"
                // struct property, whose inner field is "KeyName" (not MemberName), so the generic
                // struct handler can't see it. Emit the key name as the target (e.g. "SpaceBar").
                // Modifier flags (bControl/bAlt/bShift/bCommand) are not encoded yet — two InputKey
                // nodes differing only by modifier collapse to the same target (round-trip-neutral).
                if (nodeType == "K2Node_InputKey")
                {
                    var ik = node.Data?.FirstOrDefault(p => p.Name.ToString() == "InputKey") as StructPropertyData;
                    if (ik != null)
                    {
                        var keyName = ik.Value?.FirstOrDefault(p => p.Name.ToString() == "KeyName")?.ToString();
                        if (!string.IsNullOrEmpty(keyName) && keyName != "None")
                        {
                            return keyName;
                        }
                    }
                }

                // LatentAbilityCall (GAS ability-task async node): a UK2Node_BaseAsyncTask whose
                // identity is the factory function — ProxyFactoryFunctionName (e.g. "WaitDelay") on
                // ProxyFactoryClass (e.g. AbilityTask_WaitDelay). Emit "<FactoryClass>:<Function>"
                // (mirrors the Message/MacroInstance form) so the node can be re-spawned.
                // EnhancedInputAction (input-action event node): identity is the referenced UInputAction
                // asset. Emit its full object path so the materializer's LoadObject can reload it.
                if (nodeType == "K2Node_EnhancedInputAction")
                {
                    var ia = node.Data?.FirstOrDefault(p => p.Name.ToString() == "InputAction") as ObjectPropertyData;
                    if (ia?.Value != null && ia.Value.Index != 0)
                    {
                        return SerializeObjectRef(asset, ia.Value);
                    }
                    return null;
                }

                // K2Node_LatentGameplayTaskCall is the (non-ability) base — same ProxyFactory* identity.
                if (nodeType == "K2Node_LatentAbilityCall" || nodeType == "K2Node_LatentGameplayTaskCall")
                {
                    var fn = node.Data?.FirstOrDefault(p => p.Name.ToString() == "ProxyFactoryFunctionName")?.ToString();
                    var fc = node.Data?.FirstOrDefault(p => p.Name.ToString() == "ProxyFactoryClass") as ObjectPropertyData;
                    var cls = (fc?.Value != null && fc.Value.Index != 0) ? ResolvePackageIndex(asset, fc.Value) : null;
                    if (!string.IsNullOrEmpty(fn) && fn != "None" && !string.IsNullOrEmpty(cls))
                    {
                        return $"{cls}:{fn}";
                    }
                    return null;
                }

                if (nodeType == "K2Node_Message")
                {
                    var fref = node.Data?.FirstOrDefault(p => p.Name.ToString() == "FunctionReference") as StructPropertyData;
                    if (fref != null)
                    {
                        var mn = fref.Value?.FirstOrDefault(p => p.Name.ToString() == "MemberName")?.ToString();
                        var mp = fref.Value?.FirstOrDefault(p => p.Name.ToString() == "MemberParent") as ObjectPropertyData;
                        var cls = (mp?.Value != null && mp.Value.Index != 0) ? ResolvePackageIndex(asset, mp.Value) : null;
                        if (!string.IsNullOrEmpty(mn) && mn != "None")
                        {
                            return string.IsNullOrEmpty(cls) ? mn : $"{cls}:{mn}";
                        }
                    }
                }

                // Multicast-delegate ops (AddDelegate/RemoveDelegate/ClearDelegate/AssignDelegate): the
                // dispatcher is the DelegateReference. Emit the bare member name for a SELF-context
                // dispatcher (MemberParent absent), or "<classpath>:<member>" for an external-class
                // dispatcher (a component/widget event dispatcher) — same encoding as an external
                // CallFunction/VariableGet; the materializer SetExternalMembers it and wires the self pin.
                if (nodeType == "K2Node_AddDelegate" || nodeType == "K2Node_RemoveDelegate"
                    || nodeType == "K2Node_ClearDelegate" || nodeType == "K2Node_AssignDelegate")
                {
                    var dref = node.Data?.FirstOrDefault(p => p.Name.ToString() == "DelegateReference") as StructPropertyData;
                    if (dref != null)
                    {
                        var mn = dref.Value?.FirstOrDefault(p => p.Name.ToString() == "MemberName")?.ToString();
                        var mp = dref.Value?.FirstOrDefault(p => p.Name.ToString() == "MemberParent") as ObjectPropertyData;
                        if (!string.IsNullOrEmpty(mn) && mn != "None")
                        {
                            var cls = (mp?.Value != null && mp.Value.Index != 0) ? SerializeObjectRef(asset, mp.Value) : null;
                            return string.IsNullOrEmpty(cls) ? mn : $"{cls}:{mn}";
                        }
                    }
                    return null;
                }

                // ComponentBoundEvent (red event node bound to a component's multicast delegate, e.g. a
                // Box's OnComponentBeginOverlap). Its identity is three node fields — ComponentPropertyName
                // (the SCS component variable), DelegatePropertyName, and DelegateOwnerClass (where the
                // delegate is declared) — none recoverable from pins. We ALSO read the actual component
                // class back from the asset's SCS (the node only knows the delegate's owner, not the
                // concrete component the user attached), so re-materialization recreates the faithful
                // class. Emit a pipe-joined tuple: "<Comp>|<Delegate>|<OwnerClass>|<ComponentClass>".
                if (nodeType == "K2Node_ComponentBoundEvent")
                {
                    var compName = node.Data?.FirstOrDefault(p => p.Name.ToString() == "ComponentPropertyName")?.ToString();
                    var delName = node.Data?.FirstOrDefault(p => p.Name.ToString() == "DelegatePropertyName")?.ToString();
                    if (string.IsNullOrEmpty(compName) || compName == "None"
                        || string.IsNullOrEmpty(delName) || delName == "None")
                    {
                        return null;
                    }
                    var ownerObj = node.Data?.FirstOrDefault(p => p.Name.ToString() == "DelegateOwnerClass") as ObjectPropertyData;
                    var ownerClass = (ownerObj?.Value != null && ownerObj.Value.Index != 0)
                        ? ResolvePackageIndex(asset, ownerObj.Value) : "";

                    // Look up the SCS_Node whose InternalVariableName matches the bound component, and
                    // read its ComponentClass — the concrete class to recreate on re-materialization.
                    string compClass = "";
                    for (int ei = 0; ei < asset.Exports.Count; ei++)
                    {
                        if (!(asset.Exports[ei] is NormalExport scs)) continue;
                        if ((scs.GetExportClassType()?.ToString() ?? "") != "SCS_Node") continue;
                        var ivn = scs.Data?.FirstOrDefault(p => p.Name.ToString() == "InternalVariableName")?.ToString();
                        if (ivn != compName) continue;
                        var ccObj = scs.Data?.FirstOrDefault(p => p.Name.ToString() == "ComponentClass") as ObjectPropertyData;
                        if (ccObj?.Value != null && ccObj.Value.Index != 0)
                            compClass = ResolvePackageIndex(asset, ccObj.Value);
                        break;
                    }
                    return $"{compName}|{delName}|{ownerClass}|{compClass}";
                }

                // K2Node_CallFunction: a self/local call (bSelfContext, no MemberParent) emits a bare
                // member name (the materializer resolves it on the blueprint's own skeleton / parent
                // chain). An external or static call carries MemberParent (the owning class, e.g.
                // DataRegistrySubsystem / ListView); encode "<class>:<member>" (mirrors the Message form)
                // so the materializer can LoadClass it instead of guessing from a hardcoded library list.
                if (nodeType == "K2Node_CallFunction")
                {
                    var fref = node.Data?.FirstOrDefault(p => p.Name.ToString() == "FunctionReference") as StructPropertyData;
                    var mn = fref?.Value?.FirstOrDefault(p => p.Name.ToString() == "MemberName")?.ToString();
                    if (string.IsNullOrEmpty(mn) || mn == "None") return null;
                    var mp = fref?.Value?.FirstOrDefault(p => p.Name.ToString() == "MemberParent") as ObjectPropertyData;
                    // Full object path ("/Script/UMG.ListViewBase", "/Game/.../WBP_X.WBP_X_C") so the
                    // materializer's LoadObject reloads native and (unloaded) Blueprint-generated classes alike.
                    var cls = (mp?.Value != null && mp.Value.Index != 0) ? SerializeObjectRef(asset, mp.Value) : null;
                    return string.IsNullOrEmpty(cls) ? mn : $"{cls}:{mn}";
                }

                // K2Node_VariableGet emitted as a real node (only happens for an external member read —
                // self reads are inlined). Encode "<class>:<member>" when the VariableReference carries an
                // owning class so the materializer can set an external member reference. VariableSet uses
                // the identical encoding (a set on a component/external object via its wired self pin).
                if (nodeType == "K2Node_VariableGet" || nodeType == "K2Node_VariableSet")
                {
                    var vref = node.Data?.FirstOrDefault(p => p.Name.ToString() == "VariableReference") as StructPropertyData;
                    var mn = vref?.Value?.FirstOrDefault(p => p.Name.ToString() == "MemberName")?.ToString();
                    if (string.IsNullOrEmpty(mn) || mn == "None") return null;
                    var mp = vref?.Value?.FirstOrDefault(p => p.Name.ToString() == "MemberParent") as ObjectPropertyData;
                    var cls = (mp?.Value != null && mp.Value.Index != 0) ? SerializeObjectRef(asset, mp.Value) : null;
                    return string.IsNullOrEmpty(cls) ? mn : $"{cls}:{mn}";
                }

                if (!nodeTargetProps.TryGetValue(nodeType, out var propNames)) return null;

                foreach (var propName in propNames)
                {
                    var prop = node.Data?.FirstOrDefault(p => p.Name.ToString() == propName);
                    if (prop == null) continue;

                    // For struct properties (FunctionReference, VariableReference, etc.)
                    // look for MemberName inside
                    if (prop is StructPropertyData structProp)
                    {
                        var memberName = structProp.Value?.FirstOrDefault(p => p.Name.ToString() == "MemberName");
                        if (memberName != null)
                        {
                            var val = memberName.ToString();
                            if (!string.IsNullOrEmpty(val) && val != "None") return val;
                        }
                        // Try MemberParent for the class name
                        var memberParent = structProp.Value?.FirstOrDefault(p => p.Name.ToString() == "MemberParent");
                        if (memberParent is ObjectPropertyData objProp && objProp.Value != null && objProp.Value.Index != 0)
                        {
                            return ResolvePackageIndex(asset, objProp.Value);
                        }
                        // GraphReference (K2Node_MacroInstance.MacroGraphReference): the macro
                        // identity is the referenced macro graph's name within its owning library.
                        // MacroGraph resolves to the graph object name (e.g. "IsValid"); GraphBlueprint
                        // resolves to the library package path. Emit "<libraryPath>:<macroName>" so the
                        // reference is both readable and re-loadable.
                        var graphGuidProp = structProp.Value?.FirstOrDefault(p => p.Name.ToString() == "GraphGuid");
                        if (graphGuidProp != null)
                        {
                            var macroGraph = structProp.Value?.FirstOrDefault(p => p.Name.ToString() == "MacroGraph") as ObjectPropertyData;
                            var graphBp = structProp.Value?.FirstOrDefault(p => p.Name.ToString() == "GraphBlueprint") as ObjectPropertyData;
                            var macroName = (macroGraph?.Value != null && macroGraph.Value.Index != 0)
                                ? ResolvePackageIndex(asset, macroGraph.Value) : null;
                            var libPath = (graphBp?.Value != null && graphBp.Value.Index != 0)
                                ? ResolveObjectRef(graphBp.Value)?.ToString() : null;
                            if (!string.IsNullOrEmpty(macroName) && macroName != "None")
                            {
                                return string.IsNullOrEmpty(libPath) ? macroName : $"{libPath}:{macroName}";
                            }
                        }
                    }
                    // For name/string properties
                    else if (prop is NamePropertyData nameProp)
                    {
                        var val = nameProp.Value?.ToString();
                        if (!string.IsNullOrEmpty(val) && val != "None") return val;
                    }
                    else if (prop is StrPropertyData strProp)
                    {
                        var val = strProp.Value?.ToString();
                        if (!string.IsNullOrEmpty(val)) return val;
                    }
                    // For object references (TargetType on DynamicCast, StructType on Make/BreakStruct).
                    // Full object path so the materializer can LoadObject project-defined classes/structs
                    // (e.g. a BP cast target "WBP_X_C" or a user struct "F_AI_CircleMoveData").
                    else if (prop is ObjectPropertyData objProp2 && objProp2.Value != null && objProp2.Value.Index != 0)
                    {
                        return SerializeObjectRef(asset, objProp2.Value);
                    }
                }
                return null;
            }

            // --- Build indices ---
            // Map export index (1-based) → K2Node export
            var k2Nodes = new Dictionary<int, NormalExport>();
            // Map export index → EdGraph export
            var edGraphs = new Dictionary<int, string>();
            // Map PinId GUID → candidate (export index, pin name) list for connection resolution. A list
            // (not a single entry) because duplicated function graphs reuse pin GUIDs across graphs — the
            // collision is disambiguated at resolve time by preferring the candidate in the SAME graph as
            // the referring node (see ResolvePin).
            var pinGuidMap = new Dictionary<Guid, List<(int exportIndex, string pinName)>>();

            for (int i = 0; i < asset.Exports.Count; i++)
            {
                var export = asset.Exports[i] as NormalExport;
                if (export == null) continue;

                var classType = export.GetExportClassType()?.ToString() ?? "";
                if (classType.StartsWith("K2Node_") || classType == "K2Node")
                    k2Nodes[i + 1] = export;
                else if (classType == "EdGraph")
                    edGraphs[i + 1] = export.ObjectName.ToString();
            }

            // Group K2Nodes by parent EdGraph
            var graphNodeGroups = new Dictionary<string, List<int>>(); // graph name → list of export indices
            foreach (var (idx, node) in k2Nodes)
            {
                int outerIdx = node.OuterIndex?.Index ?? 0;
                string graphName = edGraphs.TryGetValue(outerIdx, out var name) ? name : $"Graph_{outerIdx}";
                if (!graphNodeGroups.ContainsKey(graphName))
                    graphNodeGroups[graphName] = new List<int>();
                graphNodeGroups[graphName].Add(idx);
            }

            // Reverse map (export index → owning graph) for graph-scoped pin GUID disambiguation.
            var nodeToGraph = new Dictionary<int, string>();
            foreach (var (gname, indices) in graphNodeGroups)
                foreach (var ni in indices)
                    nodeToGraph[ni] = gname;

            // --- Parse pins for all K2Nodes ---
            // Stores parsed pin data per export index
            var nodePins = new Dictionary<int, List<ParsedPin>>();
            var parseErrors = new List<object>();

            foreach (var (idx, node) in k2Nodes)
            {
                var extras = node.Extras;
                if (extras == null || extras.Length < 4)
                {
                    nodePins[idx] = new List<ParsedPin>();
                    continue;
                }

                try
                {
                    using var ms = new MemoryStream(extras);
                    using var reader = new BinaryReader(ms);

                    int pinCount = reader.ReadInt32();
                    if (pinCount < 0 || pinCount > 500)
                    {
                        parseErrors.Add(new { export_index = idx, error = $"Bad pin count: {pinCount}" });
                        nodePins[idx] = new List<ParsedPin>();
                        continue;
                    }

                    var pins = new List<ParsedPin>();
                    for (int p = 0; p < pinCount; p++)
                    {
                        var pin = ReadOnePin(reader, asset, nameMap);
                        pins.Add(pin);
                        // Register in GUID map for connection resolution (append — GUIDs may collide
                        // across duplicated graphs; ResolvePin disambiguates by graph at lookup time).
                        if (!pinGuidMap.TryGetValue(pin.PinId, out var guidList))
                        {
                            guidList = new List<(int, string)>();
                            pinGuidMap[pin.PinId] = guidList;
                        }
                        guidList.Add((idx, pin.Name));
                    }
                    nodePins[idx] = pins;
                }
                catch (Exception ex)
                {
                    parseErrors.Add(new
                    {
                        export_index = idx,
                        class_type = node.GetExportClassType()?.ToString(),
                        error = ex.Message
                    });
                    nodePins[idx] = new List<ParsedPin>();
                }
            }

            // --- Identify Knot and inlineable nodes for graph compaction ---
            var knotNodeIds = new HashSet<int>();
            var inlineNodeIds = new HashSet<int>();
            // Maps (exportIndex, pinName) → compact inline string
            var inlineMap = new Dictionary<(int, string), string>();

            foreach (var (idx, node) in k2Nodes)
            {
                var classType = node.GetExportClassType()?.ToString() ?? "";

                if (classType == "K2Node_Knot")
                {
                    knotNodeIds.Add(idx);
                }
                else if (classType == "K2Node_Self")
                {
                    inlineNodeIds.Add(idx);
                    if (nodePins.TryGetValue(idx, out var selfPins))
                    {
                        foreach (var pin in selfPins)
                        {
                            if (pin.Direction == "out")
                                inlineMap[(idx, pin.Name)] = "self";
                        }
                    }
                }
                else if (classType == "K2Node_VariableGet")
                {
                    if (nodePins.TryGetValue(idx, out var vgPins))
                    {
                        var outPins = vgPins.Where(p => p.Direction == "out").ToList();
                        // Only inline a self-context read ("var:Name"). A VariableGet that reads a member
                        // OFF an external object has a connected "self"/target input pin; inlining it would
                        // drop that input connection (the inline form carries no self), so emit it as a real
                        // node instead.
                        bool hasConnectedInput = vgPins.Any(p => p.Direction == "in" && p.LinkedTo.Count > 0);
                        if (outPins.Count <= 2 && !hasConnectedInput)
                        {
                            var varName = ResolveNodeTarget(node, classType) ?? "Unknown";
                            inlineNodeIds.Add(idx);
                            foreach (var pin in outPins)
                                inlineMap[(idx, pin.Name)] = $"var:{varName}";
                        }
                    }
                }
            }

            // Resolve a pin GUID to its target. Duplicated nodes reuse pin GUIDs (in the same graph AND
            // across graphs), so a GUID can map to several candidates. The pin's LinkedTo also carries the
            // target node's export index (preferNodeRef) — use it for an EXACT match first (disambiguates
            // intra-graph duplicates a graph-scope check can't). Fall back to same-graph, then first.
            (int exportIndex, string pinName)? ResolvePin(Guid guid, string srcGraph, int preferNodeRef)
            {
                if (!pinGuidMap.TryGetValue(guid, out var cands) || cands.Count == 0) return null;
                if (cands.Count == 1) return cands[0];
                if (preferNodeRef > 0)
                    foreach (var c in cands)
                        if (c.exportIndex == preferNodeRef) return c;
                foreach (var c in cands)
                    if (nodeToGraph.TryGetValue(c.exportIndex, out var g) && g == srcGraph) return c;
                return cands[0];
            }

            // Resolve Knot pass-throughs: follow chains of Knots to all real targets (handles fan-out)
            List<(int exportIndex, string pinName)> ResolveKnotTargets(int exportIdx, string pinName, string srcGraph, HashSet<(int, Guid)>? visited = null)
            {
                if (!knotNodeIds.Contains(exportIdx))
                    return new List<(int, string)> { (exportIdx, pinName) };
                if (!nodePins.TryGetValue(exportIdx, out var knotPins))
                    return new List<(int, string)>();

                // Find the pin we arrived at
                var arrivedPin = knotPins.FirstOrDefault(p => p.Name == pinName);
                if (arrivedPin.Name == null) return new List<(int, string)>();

                // Follow through to the OTHER direction pin (in→out, out→in)
                var otherDir = arrivedPin.Direction == "in" ? "out" : "in";
                var otherPin = knotPins.FirstOrDefault(p => p.Direction == otherDir);
                if (otherPin.Name == null || otherPin.LinkedTo.Count == 0) return new List<(int, string)>();

                visited ??= new HashSet<(int, Guid)>();
                var results = new List<(int, string)>();
                foreach (var (nextNodeRef, nextPinGuid) in otherPin.LinkedTo)
                {
                    if (!visited.Add((nextNodeRef, nextPinGuid))) continue; // cycle
                    var next = ResolvePin(nextPinGuid, srcGraph, nextNodeRef);
                    if (next != null)
                        results.AddRange(ResolveKnotTargets(next.Value.exportIndex, next.Value.pinName, srcGraph, visited));
                }
                return results;
            }

            // AddComponent's component-template overrides: the hidden TemplateName pin names the
            // archetype template object in the package (UBlueprint::ComponentTemplates); read its
            // serialized (= non-default) properties as the override set. These live outside the graph,
            // so without this the spawned component's mesh/material/etc. configuration is lost.
            Dictionary<string, string>? ExtractComponentOverrides(List<ParsedPin> compPins)
            {
                var tnPin = compPins.FirstOrDefault(p => p.Name == "TemplateName");
                var templateName = tnPin.DefaultValue;
                if (string.IsNullOrEmpty(templateName)) return null;
                var tmpl = asset.Exports.OfType<NormalExport>()
                    .FirstOrDefault(e => e.ObjectName.ToString() == templateName);
                if (tmpl?.Data == null) return null;
                var overrides = new Dictionary<string, string>();
                foreach (var p in tmpl.Data)
                {
                    var pname = p.Name?.ToString();
                    if (string.IsNullOrEmpty(pname)) continue;
                    var v = SerializePropertyValue(asset, p);
                    if (v != null) overrides[pname] = v;
                }
                return overrides.Count > 0 ? overrides : null;
            }

            // Timeline (UK2Node_Timeline): the node names a member variable; its real data lives in a
            // UTimelineTemplate (UBlueprint::Timelines, matched by VariableName) + embedded UCurve*
            // objects — all outside the graph. Walk the template's 4 track arrays, resolve each track's
            // curve export, and read its FRichCurve keyframes.
            void AddTimelineTracks(GraphTimelineData tl, NormalExport tmpl, string arrayProp, string kind,
                string curveRefProp, string curvePropName)
            {
                var arr = tmpl.Data?.FirstOrDefault(p => p.Name.ToString() == arrayProp) as ArrayPropertyData;
                if (arr?.Value == null) return;
                foreach (var elem in arr.Value)
                {
                    if (elem is not StructPropertyData track) continue;
                    var name = track.Value?.FirstOrDefault(p => p.Name.ToString() == "TrackName")?.ToString() ?? "";
                    var curveRef = track.Value?.FirstOrDefault(p => p.Name.ToString() == curveRefProp) as ObjectPropertyData;
                    var t = new GraphTimelineTrack { Name = name, Kind = kind };
                    if (curveRef?.Value != null && curveRef.Value.IsExport())
                    {
                        if (curveRef.Value.ToExport(asset) is NormalExport curveExport)
                            t.Curves = ReadCurves(curveExport, curvePropName);
                    }
                    tl.Tracks.Add(t);
                }
            }

            GraphTimelineData? ExtractTimeline(NormalExport node)
            {
                var varName = (node.Data?.FirstOrDefault(p => p.Name.ToString() == "TimelineName") as NamePropertyData)?.Value?.ToString();
                if (string.IsNullOrEmpty(varName)) return null;
                var tmpl = asset.Exports.OfType<NormalExport>().FirstOrDefault(e =>
                    (e.GetExportClassType()?.ToString() ?? "") == "TimelineTemplate" &&
                    (e.Data?.FirstOrDefault(p => p.Name.ToString() == "VariableName") as NamePropertyData)?.Value?.ToString() == varName);
                if (tmpl == null) return null;

                var tl = new GraphTimelineData();
                tl.Length = (tmpl.Data?.FirstOrDefault(p => p.Name.ToString() == "TimelineLength") as FloatPropertyData)?.Value ?? 0f;
                tl.Loop = (tmpl.Data?.FirstOrDefault(p => p.Name.ToString() == "bLoop") as BoolPropertyData)?.Value ?? false;
                tl.Autoplay = (tmpl.Data?.FirstOrDefault(p => p.Name.ToString() == "bAutoPlay") as BoolPropertyData)?.Value ?? false;
                tl.Replicated = (tmpl.Data?.FirstOrDefault(p => p.Name.ToString() == "bReplicated") as BoolPropertyData)?.Value ?? false;
                tl.IgnoreTimeDilation = (tmpl.Data?.FirstOrDefault(p => p.Name.ToString() == "bIgnoreTimeDilation") as BoolPropertyData)?.Value ?? false;
                // UTimelineTemplate's LengthMode CDO default is TL_TimelineLength(0); it's only
                // serialized when non-default. Absent => the default TimelineLength. (Note: the
                // materializer currently coerces requested LastKeyFrame to TimelineLength, so this
                // round-trips deterministically as TimelineLength either way.)
                var lm = tmpl.Data?.FirstOrDefault(p => p.Name.ToString() == "LengthMode") as BytePropertyData;
                tl.LengthMode = (lm != null && lm.Value == 1) ? "LastKeyFrame" : "TimelineLength";

                AddTimelineTracks(tl, tmpl, "FloatTracks", "float", "CurveFloat", "FloatCurve");
                AddTimelineTracks(tl, tmpl, "VectorTracks", "vector", "CurveVector", "FloatCurves");
                AddTimelineTracks(tl, tmpl, "LinearColorTracks", "color", "CurveLinearColor", "FloatCurves");
                AddTimelineTracks(tl, tmpl, "EventTracks", "event", "CurveKeys", "FloatCurve");
                return tl;
            }

            // Asset name. Match the blueprint asset export by a class name that ENDS WITH "Blueprint"
            // (Blueprint / WidgetBlueprint / AnimBlueprint) — not merely Contains, which would wrongly
            // grab MVVM extension exports like "MVVMBlueprintView" / "MVVMBlueprintViewSettings".
            var bpExport = asset.Exports
                .OfType<NormalExport>()
                .FirstOrDefault(e => e.GetExportClassType()?.ToString()?.EndsWith("Blueprint") == true);
            var bpName = bpExport?.ObjectName.ToString()
                ?? Path.GetFileNameWithoutExtension(ProgramContext.assetPath);

            // BP parent class (so a non-Actor base round-trips). Stored as an ObjectProperty on the
            // Blueprint export; resolve to a full path the materializer's LoadClass can reload.
            var parentProp = bpExport?.Data?.FirstOrDefault(p => p.Name.ToString() == "ParentClass") as ObjectPropertyData;
            var parentClass = parentProp?.Value != null ? SerializeObjectRef(asset, parentProp.Value) : null;

            var functions = new List<GraphFunctionData>();

            foreach (var (graphName, nodeIndices) in graphNodeGroups)
            {
                var functionNodes = new List<GraphNodeData>();

                foreach (var nodeIdx in nodeIndices)
                {
                    if (!k2Nodes.TryGetValue(nodeIdx, out var node)) continue;
                    if (!nodePins.TryGetValue(nodeIdx, out var pins)) continue;
                    if (knotNodeIds.Contains(nodeIdx) || inlineNodeIds.Contains(nodeIdx)) continue;

                    var classType = node.GetExportClassType()?.ToString() ?? "";
                    var shortType = classType.StartsWith("K2Node_") ? classType.Substring(7) : classType;
                    var target = ResolveNodeTarget(node, classType);

                    // GetClassDefaults reads a class's CDO; the class lives on the "Class" input pin's
                    // DefaultObject (a literal class ref, not a node property), so recover it from there.
                    if (classType == "K2Node_GetClassDefaults" && string.IsNullOrEmpty(target))
                    {
                        var classPin = pins.FirstOrDefault(p => p.Name == "Class" && p.Direction == "in");
                        if (!string.IsNullOrEmpty(classPin.DefaultObjectRef))
                            target = classPin.DefaultObjectRef;
                    }

                    // Early-exit: skip nodes with zero connections and no meaningful pins
                    bool hasAnyConnection = pins.Any(p => p.LinkedTo.Count > 0);
                    if (!hasAnyConnection) continue;

                    var nodePinsList = new List<GraphPinData>();

                    foreach (var pin in pins)
                    {
                        // Skip hidden and orphaned pins
                        if (pin.IsHidden || pin.IsOrphaned) continue;

                        // Skip self input pins with no connections (noise)
                        if (pin.Name == "self" && pin.Direction == "in" && pin.LinkedTo.Count == 0)
                            continue;

                        // On a signature-defining node (CustomEvent / FunctionEntry / FunctionResult) every
                        // data pin IS a parameter of the function/event signature, so keep it even when
                        // unconnected and default-less — dropping it would shorten the signature (e.g. a
                        // delegate-bound custom event whose Tag param is unwired) and fail to compile.
                        bool isSignatureNode = classType == "K2Node_CustomEvent"
                            || classType == "K2Node_FunctionEntry" || classType == "K2Node_FunctionResult";
                        bool isParamPin = pin.Category != "exec" && pin.Category != "delegate" && pin.Name != "self";

                        // Skip unconnected pins with no user-set default (just node shape declarations).
                        // A PC_Text default (TextDefault) or a class/object-literal default (DefaultObject,
                        // e.g. a DeterminesOutputType ComponentClass pin) counts as a user-set default too.
                        if (pin.LinkedTo.Count == 0 && string.IsNullOrWhiteSpace(pin.DefaultValue)
                            && string.IsNullOrEmpty(pin.TextDefault) && string.IsNullOrEmpty(pin.DefaultObjectRef)
                            && !(isSignatureNode && isParamPin))
                            continue;

                        var pinData = new GraphPinData
                        {
                            Name = pin.Name,
                            Dir = pin.Direction,
                            Cat = pin.Category,
                        };

                        if (!string.IsNullOrEmpty(pin.SubCategoryObject))
                            pinData.Sub = pin.SubCategoryObject;
                        if (pin.ContainerType == 1) pinData.Container = "array";
                        else if (pin.ContainerType == 2) pinData.Container = "set";
                        else if (pin.ContainerType == 3)
                        {
                            pinData.Container = "map";
                            if (!string.IsNullOrEmpty(pin.ValueCategory)) pinData.ValueCat = pin.ValueCategory;
                            if (!string.IsNullOrEmpty(pin.ValueSubObject)) pinData.ValueSub = pin.ValueSubObject;
                        }
                        if (!string.IsNullOrEmpty(pin.DefaultValue))
                            pinData.Default = pin.DefaultValue;
                        if (!string.IsNullOrEmpty(pin.TextDefault))
                            pinData.TextDefault = pin.TextDefault;
                        if (!string.IsNullOrEmpty(pin.DefaultObjectRef))
                            pinData.DefaultObj = pin.DefaultObjectRef;
                        // Reference/const flags on a signature param (e.g. a const-ref delegate-bound custom
                        // event param) — needed so the rebuilt user-defined pin matches the bound signature.
                        if (isSignatureNode && isParamPin && pin.IsReference) pinData.Ref = true;
                        if (isSignatureNode && isParamPin && pin.IsConst) pinData.Const = true;

                        // Resolve connections: follow through Knots, substitute inline refs
                        if (pin.LinkedTo.Count > 0)
                        {
                            var targets = new List<string>();
                            foreach (var (linkedNodeRef, linkedPinGuid) in pin.LinkedTo)
                            {
                                var resolved = ResolvePin(linkedPinGuid, graphName, linkedNodeRef);
                                if (resolved == null)
                                {
                                    targets.Add($"{linkedNodeRef}:{linkedPinGuid}");
                                    continue;
                                }

                                // Follow through Knot nodes to find all real targets (handles fan-out)
                                var finals = ResolveKnotTargets(resolved.Value.exportIndex, resolved.Value.pinName, graphName);
                                foreach (var (finalIdx, finalPin) in finals)
                                {
                                    // Substitute inline references for VariableGet/Self nodes
                                    if (inlineMap.TryGetValue((finalIdx, finalPin), out var inlineRef))
                                        targets.Add(inlineRef);
                                    else
                                        targets.Add($"{finalIdx}:{finalPin}");
                                }
                            }
                            if (targets.Count > 0)
                                pinData.To = targets;
                        }

                        nodePinsList.Add(pinData);
                    }

                    var overrides = classType == "K2Node_AddComponent"
                        ? ExtractComponentOverrides(pins) : null;
                    var timeline = classType == "K2Node_Timeline"
                        ? ExtractTimeline(node) : null;

                    // A VariableGet can be impure (a "validated get" with exec pins): CurrentVariation is
                    // Pure | ValidatedObject | Branch. Capture the non-Pure variation so the materializer
                    // reproduces the impure pin shape (a pure default would lack the exec pins).
                    string variation = null;
                    if (classType == "K2Node_VariableGet")
                    {
                        var cv = node.Data?.FirstOrDefault(p => p.Name.ToString() == "CurrentVariation");
                        var cvStr = cv?.ToString();
                        if (!string.IsNullOrEmpty(cvStr) && cvStr != "None")
                        {
                            var shortV = cvStr.Contains("::") ? cvStr.Substring(cvStr.LastIndexOf("::") + 2) : cvStr;
                            if (shortV != "Pure") variation = shortV;
                        }
                    }

                    functionNodes.Add(new GraphNodeData
                    {
                        Id = nodeIdx,
                        Type = shortType,
                        Target = target,
                        Pins = nodePinsList,
                        Overrides = overrides,
                        Timeline = timeline,
                        Variation = variation,
                    });
                }

                if (functionNodes.Count > 0)
                {
                    functions.Add(new GraphFunctionData
                    {
                        Name = graphName,
                        Nodes = functionNodes,
                    });
                }
            }

            // Widget Blueprint visual hierarchy. The UWidgetBlueprint export's WidgetTree -> RootWidget is
            // the template root; recurse through each panel's Slots (slot.Content = child widget). Property
            // deltas reuse SerializePropertyValue; structural/editor-only fields are filtered out.
            GraphWidgetData widgetTree = null;
            {
                NormalExport ExportAt(FPackageIndex idx) =>
                    (idx != null && idx.Index > 0 && idx.Index <= asset.Exports.Count)
                        ? asset.Exports[idx.Index - 1] as NormalExport : null;
                PropertyData NamedProp(NormalExport e, string name) =>
                    e?.Data?.FirstOrDefault(p => p.Name?.ToString() == name);
                var skipWidgetProps = new HashSet<string> { "Slot", "Slots", "Content", "Parent", "bIsVariable", "bExpandedInDesigner", "DisplayLabel", "WidgetTree", "RootWidget", "Navigation" };
                var skipSlotProps = new HashSet<string> { "Content", "Parent" };
                Dictionary<string, string> PropDeltas(NormalExport e, HashSet<string> skip)
                {
                    var d = new Dictionary<string, string>();
                    foreach (var p in e.Data ?? new List<PropertyData>())
                    {
                        var pn = p.Name?.ToString() ?? "";
                        if (skip.Contains(pn)) continue;
                        var v = SerializePropertyValue(asset, p);
                        // Integrity: never DROP a serialized property. If we can't render its value
                        // (complex nested struct/array not yet supported), emit a loud marker instead so
                        // a clone that omits it surfaces as a real diff rather than matching silently.
                        d[pn] = v ?? $"<unserialized:{p.GetType().Name}>";
                    }
                    return d.Count > 0 ? d : null;
                }
                GraphWidgetData BuildWidget(NormalExport widgetExp, NormalExport slotExp)
                {
                    var w = new GraphWidgetData
                    {
                        Name = widgetExp.ObjectName.ToString(),
                        // Full class path ("/Script/UMG.CanvasPanel" or "/Game/.../WBP_X.WBP_X_C") so the
                        // materializer can LoadObject unloaded Blueprint-generated widget classes.
                        Class = SerializeObjectRef(asset, widgetExp.ClassIndex) ?? widgetExp.GetExportClassType()?.ToString() ?? "",
                        // UWidget::bIsVariable defaults to TRUE (Widget.cpp), so an absent property means
                        // the widget IS a variable. The compiler only serializes bIsVariable=false for
                        // generated-name widgets it demotes. Defaulting to false here would wrongly drop
                        // the variable flag from every named widget (and break ComponentBoundEvent binds).
                        IsVariable = (NamedProp(widgetExp, "bIsVariable") as BoolPropertyData)?.Value ?? true,
                        Properties = PropDeltas(widgetExp, skipWidgetProps),
                    };
                    if (slotExp != null)
                    {
                        w.SlotClass = slotExp.GetExportClassType()?.ToString();
                        w.SlotProperties = PropDeltas(slotExp, skipSlotProps);
                    }
                    // Navigation: an instanced UWidgetNavigation sub-object. Capture its per-direction
                    // FWidgetNavigationData rules (the bare "Navigation=WidgetNavigation_0" reference, in
                    // skipWidgetProps, carries no rules) so the materializer recreates them faithfully.
                    var navExp = ExportAt((NamedProp(widgetExp, "Navigation") as ObjectPropertyData)?.Value);
                    if (navExp != null)
                    {
                        var nav = new Dictionary<string, string>();
                        foreach (var p in navExp.Data ?? new List<PropertyData>())
                        {
                            var v = SerializePropertyValue(asset, p);
                            // Skip default-rule directions: FWidgetNavigationData.Rule defaults to Escape
                            // (WidgetNavigation.h), so a bare "(Rule=...Escape)" is semantically "no rule".
                            // The original may serialize it explicitly while a clone omits it (default not
                            // written) — filtering keeps only meaningful rules so the IR round-trips.
                            if (v != null && v != "(Rule=EUINavigationRule::Escape)") nav[p.Name.ToString()] = v;
                        }
                        if (nav.Count > 0) w.Navigation = nav;
                    }
                    if (NamedProp(widgetExp, "Slots") is ArrayPropertyData slotsArr)
                    {
                        var children = new List<GraphWidgetData>();
                        foreach (var slotRef in slotsArr.Value.OfType<ObjectPropertyData>())
                        {
                            var childSlotExp = ExportAt(slotRef.Value);
                            var contentExp = ExportAt((NamedProp(childSlotExp, "Content") as ObjectPropertyData)?.Value);
                            if (contentExp != null) children.Add(BuildWidget(contentExp, childSlotExp));
                        }
                        if (children.Count > 0) w.Children = children;
                    }
                    return w;
                }
                var wbpExport = asset.Exports.OfType<NormalExport>()
                    .FirstOrDefault(e => e.GetExportClassType()?.ToString() == "WidgetBlueprint");
                var wtExp = ExportAt((NamedProp(wbpExport, "WidgetTree") as ObjectPropertyData)?.Value);
                var rootExp = ExportAt((NamedProp(wtExp, "RootWidget") as ObjectPropertyData)?.Value);
                if (rootExp != null) widgetTree = BuildWidget(rootExp, null);
            }

            // SimpleConstructionScript components (Actor/Pawn/Character blueprints). Each SCS_Node names a
            // ComponentClass, an InternalVariableName, a ComponentTemplate (holding property deltas), and a
            // parent: BP->native via ParentComponentOrVariableName(+bIsParentComponentNative), BP->BP via the
            // parent node's ChildNodes. Normalize to a child-centric model so the materializer reconstructs it.
            List<GraphComponentData> components = null;
            {
                NormalExport ExportAt2(FPackageIndex idx) =>
                    (idx != null && idx.Index > 0 && idx.Index <= asset.Exports.Count)
                        ? asset.Exports[idx.Index - 1] as NormalExport : null;
                PropertyData NamedProp2(NormalExport e, string name) =>
                    e?.Data?.FirstOrDefault(p => p.Name?.ToString() == name);

                var scsNodes = asset.Exports.OfType<NormalExport>()
                    .Where(e => (e.GetExportClassType()?.ToString() ?? "") == "SCS_Node").ToList();
                if (scsNodes.Count > 0)
                {
                    // DefaultSceneRootNode of the SimpleConstructionScript export.
                    var scsExp = asset.Exports.OfType<NormalExport>()
                        .FirstOrDefault(e => (e.GetExportClassType()?.ToString() ?? "") == "SimpleConstructionScript");
                    var defaultRootName = ((NamedProp2(scsExp, "DefaultSceneRootNode") as ObjectPropertyData)?.Value is FPackageIndex dri && dri.Index != 0)
                        ? (ExportAt2(dri)?.ObjectName.ToString()) : null;

                    string VarName(NormalExport n) =>
                        (NamedProp2(n, "InternalVariableName") as NamePropertyData)?.Value.ToString()
                        ?? n.ObjectName.ToString();

                    // Map each child node's export-name -> its BP parent's variable name (via ChildNodes).
                    var bpParentOf = new Dictionary<string, string>();
                    foreach (var n in scsNodes)
                    {
                        if (NamedProp2(n, "ChildNodes") is ArrayPropertyData kids)
                        {
                            foreach (var childRef in kids.Value.OfType<ObjectPropertyData>())
                            {
                                var childExp = ExportAt2(childRef.Value);
                                if (childExp != null)
                                    bpParentOf[childExp.ObjectName.ToString()] = VarName(n);
                            }
                        }
                    }

                    var skipCompProps = new HashSet<string> { "AttachParent", "AttachChildren", "AttachSocketName", "bIsParentComponentNative" };
                    Dictionary<string, string> CompPropDeltas(NormalExport tmpl)
                    {
                        if (tmpl == null) return null;
                        var d = new Dictionary<string, string>();
                        foreach (var p in tmpl.Data ?? new List<PropertyData>())
                        {
                            var pn = p.Name?.ToString() ?? "";
                            if (skipCompProps.Contains(pn)) continue;
                            var v = SerializePropertyValue(asset, p);
                            d[pn] = v ?? $"<unserialized:{p.GetType().Name}>";
                        }
                        return d.Count > 0 ? d : null;
                    }

                    components = new List<GraphComponentData>();
                    foreach (var n in scsNodes)
                    {
                        var name = VarName(n);
                        var classPath = SerializeObjectRef(asset, (NamedProp2(n, "ComponentClass") as ObjectPropertyData)?.Value)
                            ?? "/Script/Engine.SceneComponent";
                        var nativeParent = (NamedProp2(n, "ParentComponentOrVariableName") as NamePropertyData)?.Value.ToString();
                        var isNative = (NamedProp2(n, "bIsParentComponentNative") as BoolPropertyData)?.Value ?? false;
                        var socket = (NamedProp2(n, "AttachToName") as NamePropertyData)?.Value.ToString();
                        var tmpl = ExportAt2((NamedProp2(n, "ComponentTemplate") as ObjectPropertyData)?.Value);

                        string parentName = (isNative && !string.IsNullOrEmpty(nativeParent))
                            ? nativeParent
                            : (bpParentOf.TryGetValue(n.ObjectName.ToString(), out var bp) ? bp : null);

                        components.Add(new GraphComponentData
                        {
                            Name = name,
                            Class = classPath,
                            ParentName = parentName,
                            ParentNative = isNative && !string.IsNullOrEmpty(nativeParent),
                            AttachSocket = string.IsNullOrEmpty(socket) ? null : socket,
                            IsDefaultSceneRoot = (name == defaultRootName) || (n.ObjectName.ToString() == defaultRootName),
                            Properties = CompPropDeltas(tmpl),
                        });
                    }
                }
            }

            return new GraphData
            {
                Name = bpName,
                ParentClass = parentClass,
                Functions = functions,
                WidgetTree = widgetTree,
                Components = components,
                Errors = parseErrors.Count > 0 ? parseErrors : null,
            };
        }

        public static void ExtractGraph(UAsset asset, string outputFormat)
        {
            var graphData = BuildGraphData(asset);

            if (outputFormat == "json")
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                };
                Console.Write(JsonSerializer.Serialize(graphData, options));
                return;
            }

            // XML output
            var xml = new System.Text.StringBuilder();
            xml.AppendLine("<graph>");
            xml.AppendLine($"  <name>{EscapeXml(graphData.Name)}</name>");

            foreach (var function in graphData.Functions)
            {
                xml.AppendLine($"  <function name=\"{EscapeXml(function.Name)}\">");
                foreach (var node in function.Nodes)
                {
                    var targetAttr = !string.IsNullOrEmpty(node.Target) ? $" target=\"{EscapeXml(node.Target)}\"" : "";
                    xml.AppendLine($"    <node id=\"{node.Id}\" type=\"{EscapeXml(node.Type)}\"{targetAttr}>");

                    foreach (var pin in node.Pins)
                    {
                        var attrs = new System.Text.StringBuilder();
                        attrs.Append($" name=\"{EscapeXml(pin.Name)}\" dir=\"{pin.Dir}\" cat=\"{EscapeXml(pin.Cat)}\"");

                        if (!string.IsNullOrEmpty(pin.Sub))
                            attrs.Append($" sub=\"{EscapeXml(pin.Sub)}\"");
                        if (!string.IsNullOrEmpty(pin.Container))
                            attrs.Append($" container=\"{pin.Container}\"");
                        if (!string.IsNullOrEmpty(pin.Default))
                            attrs.Append($" default=\"{EscapeXml(pin.Default)}\"");
                        if (pin.To != null && pin.To.Count > 0)
                            attrs.Append($" to=\"{EscapeXml(string.Join(",", pin.To))}\"");

                        xml.AppendLine($"      <pin{attrs}/>");
                    }

                    xml.AppendLine("    </node>");
                }
                xml.AppendLine("  </function>");
            }

            // Parse errors as XML comments
            if (graphData.Errors != null && graphData.Errors.Count > 0)
            {
                foreach (var err in graphData.Errors)
                {
                    var errStr = JsonSerializer.Serialize(err);
                    xml.AppendLine($"  <!-- error: {EscapeXml(errStr)} -->");
                }
            }

            xml.AppendLine("</graph>");
            Console.Write(xml.ToString());
        }
        
        
    }
}
