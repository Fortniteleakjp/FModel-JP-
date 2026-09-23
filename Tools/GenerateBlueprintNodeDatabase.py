"""
Builds FModel/Resources/BlueprintNodeDatabase.json.gz, the table the blueprint graph viewer names its nodes with.

Cooked games keep no editor metadata for native functions, the node titles (DisplayName, CompactNodeTitle),
the pins (parameter names, UPARAM display names, hidden pins) and whether a call is pure all live in the
UFUNCTION declarations of the engine headers. This script reads them from an Unreal Engine checkout.

    python Tools/GenerateBlueprintNodeDatabase.py <UnrealEngine checkout> [output]
        [--dumpspace FunctionsInfo.json.gz] [--dumpspace-classes ClassesInfo.json.gz]

Only headers are needed, a sparse checkout of Engine/Source/Runtime/**/*.h and Engine/Plugins/**/*.h is enough.
The game's own classes are not in the engine source: an SDK dump from Dumpspace
(https://github.com/Spuckwaffel/dumpspace, Games/Unreal-Engine-5/Fortnite/*.json.gz) adds their functions,
the class hierarchy and the types of object members.
"""

import gzip
import json
import os
import re
import subprocess
import sys

API_WORD = re.compile(r"^[A-Z0-9_]+_API$")
IDENT = re.compile(r"[A-Za-z_]\w*")
DECL_KEYWORDS = {
    "static", "virtual", "inline", "FORCEINLINE", "FORCENOINLINE", "explicit", "constexpr", "UE_API",
    "UE_INL_API", "FORCEINLINE_DEBUGGABLE", "CONSTEXPR", "extern", "friend",
}


def strip_comments(text):
    out = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c == '"' or c == "'":
            j = i + 1
            while j < n and text[j] != c:
                j += 2 if text[j] == "\\" else 1
            out.append(text[i:j + 1])
            i = j + 1
        elif text.startswith("//", i):
            j = text.find("\n", i)
            i = n if j < 0 else j
        elif text.startswith("/*", i):
            j = text.find("*/", i + 2)
            i = n if j < 0 else j + 2
        else:
            out.append(c)
            i += 1
    return "".join(out)


def balanced(text, start):
    """text[start] is '(' -> index of the matching ')'."""
    depth = 0
    i = start
    n = len(text)
    while i < n:
        c = text[i]
        if c == '"':
            j = i + 1
            while j < n and text[j] != '"':
                j += 2 if text[j] == "\\" else 1
            i = j
        elif c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return -1


def split_top(text, sep=","):
    parts, depth, cur, i = [], 0, [], 0
    while i < len(text):
        c = text[i]
        if c == '"':
            j = i + 1
            while j < len(text) and text[j] != '"':
                j += 2 if text[j] == "\\" else 1
            cur.append(text[i:j + 1])
            i = j + 1
            continue
        if c in "(<[{":
            depth += 1
        elif c in ")>]}":
            depth -= 1
        if c == sep and depth == 0:
            parts.append("".join(cur))
            cur = []
        else:
            cur.append(c)
        i += 1
    if "".join(cur).strip():
        parts.append("".join(cur))
    return parts


def unquote(value):
    value = value.strip()
    if len(value) >= 2 and value[0] == '"' and value[-1] == '"':
        value = value[1:-1]
    return value.replace('\\"', '"')


def parse_specifiers(text):
    """UFUNCTION/UPARAM/UCLASS arguments -> (flags set, meta dict)."""
    flags, meta = set(), {}
    for part in split_top(text):
        part = part.strip()
        if not part:
            continue
        key, _, value = part.partition("=")
        key = key.strip()
        if key.lower() == "meta":
            value = value.strip()
            if value.startswith("(") and value.endswith(")"):
                value = value[1:-1]
            for entry in split_top(value):
                k, _, v = entry.partition("=")
                if k.strip():
                    meta[k.strip()] = unquote(v) if v else "true"
        elif value:
            if value.strip().lower() != "false":
                flags.add(key)
            meta.setdefault(key, unquote(value))
        else:
            flags.add(key)
    return flags, meta


def script_name(cpp_name):
    if len(cpp_name) > 1 and cpp_name[0] in "UAIF" and cpp_name[1].isupper():
        return cpp_name[1:]
    return cpp_name


def parse_param(text):
    text = text.strip()
    if not text or text == "void":
        return None

    uparam_flags, uparam_meta = set(), {}
    m = re.search(r"\bUPARAM\s*\(", text)
    if m:
        end = balanced(text, m.end() - 1)
        uparam_flags, uparam_meta = parse_specifiers(text[m.end():end])
        text = text[:m.start()] + text[end + 1:]

    # default value
    default = None
    parts = split_top(text, "=")
    if len(parts) > 1:
        text, default = parts[0], "=".join(parts[1:]).strip()

    text = text.strip()
    names = IDENT.findall(text)
    if not names:
        return None
    name = names[-1]
    type_text = text[: text.rfind(name)].strip()
    is_ref = "&" in type_text
    is_const = re.search(r"\bconst\b", type_text) is not None
    is_out = is_ref and not is_const and "ref" not in uparam_flags

    return {
        "name": name,
        "type": re.sub(r"\s+", " ", type_text),
        "out": is_out,
        "bool": re.fullmatch(r"(const\s+)?bool\s*&?", type_text) is not None,
        "display": uparam_meta.get("DisplayName"),
        "default": default,
    }


def parse_declaration(decl):
    """'static bool EqualEqual_ByteByte(uint8 A, uint8 B)' -> (name, return type, params, is_static)."""
    i = 0
    while True:
        p = decl.find("(", i)
        if p < 0:
            return None
        before = decl[:p].rstrip()
        m = re.search(r"([A-Za-z_]\w*)$", before)
        if not m:
            return None
        ident = m.group(1)
        end = balanced(decl, p)
        if end < 0:
            return None
        # macros such as UE_DEPRECATED(...) or PURE_VIRTUAL(...) are not the parameter list
        if re.fullmatch(r"[A-Z0-9_]+", ident):
            i = end + 1
            continue
        head = before[: m.start()]
        is_static = re.search(r"\bstatic\b", head) is not None
        ret_words = [w for w in head.split() if w not in DECL_KEYWORDS and not API_WORD.match(w)]
        ret = " ".join(ret_words).strip()
        params = [x for x in (parse_param(t) for t in split_top(decl[p + 1:end])) if x]
        return ident, ret, params, is_static


def matching_brace(text, start):
    """text[start] is '{' -> index of the matching '}'."""
    depth, i, n = 0, start, len(text)
    while i < n:
        c = text[i]
        if c == '"' or c == "'":
            j = i + 1
            while j < n and text[j] != c:
                j += 2 if text[j] == "\\" else 1
            i = j
        elif c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return n


def is_reflected(cpp, db):
    """A UCLASS, or the I-class of a UINTERFACE (whose U-class sits in the same header)."""
    return script_name(cpp) in db


def parse_header(path, db, structs):
    try:
        text = open(path, encoding="utf-8", errors="replace").read()
    except OSError:
        return
    if "UFUNCTION" not in text and "USTRUCT" not in text and "UCLASS" not in text:
        return
    text = strip_comments(text)

    # every class/struct opening, to know which one a UFUNCTION sits in
    class_re = re.compile(r"\b(class|struct)\s+(?:[A-Z0-9_]+_API\s+)?(?:alignas\([^)]*\)\s+)?(?:UE_DEPRECATED\([^)]*\)\s+)?([A-Za-z_]\w*)\s*(?:final\s*)?(?::[^;{]*)?\{")
    classes = []
    for m in class_re.finditer(text):
        body = m.end() - 1
        classes.append((body, matching_brace(text, body), m.group(2)))

    # class and struct metadata
    for m in re.finditer(r"\b(UCLASS|UINTERFACE|USTRUCT)\s*\(", text):
        end = balanced(text, m.end() - 1)
        if end < 0:
            continue
        flags, meta = parse_specifiers(text[m.end():end])
        nxt = class_re.search(text, end)
        if not nxt:
            continue
        cpp = nxt.group(2)
        entry = {}
        if "DisplayName" in meta:
            entry["d"] = meta["DisplayName"]
        if m.group(1) == "USTRUCT":
            structs[script_name(cpp)] = entry
        else:
            cls = db.setdefault(script_name(cpp), {"f": {}})
            cls.update(entry)
            if m.group(1) == "UINTERFACE":
                cls["i"] = 1

    for m in re.finditer(r"\bUFUNCTION\s*\(", text):
        end = balanced(text, m.end() - 1)
        if end < 0:
            continue
        flags, meta = parse_specifiers(text[m.end():end])
        if not flags & {"BlueprintCallable", "BlueprintPure", "BlueprintImplementableEvent", "BlueprintNativeEvent", "BlueprintGetter", "BlueprintSetter"}:
            continue

        # declaration up to ';' or '{' outside parentheses
        depth, j = 0, end + 1
        while j < len(text):
            c = text[j]
            if c == "(":
                depth += 1
            elif c == ")":
                depth -= 1
            elif c in ";{" and depth == 0:
                break
            j += 1
        parsed = parse_declaration(text[end + 1:j])
        if not parsed:
            continue
        name, ret, params, is_static = parsed

        # innermost reflected class around the declaration, nested plain structs are skipped
        owner = None
        for start, stop, cpp in classes:
            if start < m.start() < stop and is_reflected(cpp, db):
                owner = cpp
        if owner is None:
            continue

        hidden = set()
        for key in ("WorldContext", "LatentInfo", "HidePin", "CallableWithoutWorldContext"):
            if key in meta and key != "CallableWithoutWorldContext":
                hidden.update(x.strip() for x in meta[key].split(","))
        internal = "BlueprintInternalUseOnly" in meta
        pure = "BlueprintPure" in flags or ("BlueprintGetter" in flags)
        # UHT (UhtFunction.cs) turns a const BlueprintCallable with any output into a pure function,
        # unless it says BlueprintPure=false
        has_outputs = (ret and ret != "void") or any(p["out"] for p in params)
        if "BlueprintCallable" in flags and has_outputs and re.search(r"\)\s*const\b", text[end + 1:j]):
            pure = True
        if "BlueprintPure" in meta and str(meta.get("BlueprintPure")).lower() == "false":
            pure = False

        fn = {}
        if "DisplayName" in meta:
            fn["d"] = meta["DisplayName"]
        if "CompactNodeTitle" in meta:
            fn["c"] = meta["CompactNodeTitle"]
        if pure:
            fn["pure"] = 1
        if is_static:
            fn["static"] = 1
        if "Latent" in meta:
            fn["latent"] = 1
        if internal:
            fn["internal"] = 1
        if flags & {"BlueprintImplementableEvent", "BlueprintNativeEvent"}:
            fn["event"] = 1
        if "ReturnDisplayName" in meta:
            fn["rd"] = meta["ReturnDisplayName"]
        for key in ("ExpandEnumAsExecs", "ExpandBoolAsExecs", "DefaultToSelf"):
            if key in meta:
                fn[key] = meta[key]
        if ret and ret != "void":
            fn["r"] = 2 if re.fullmatch(r"(const\s+)?bool", ret) else 1
            if re.fullmatch(r"(const\s+)?bool", ret) is None and ret:
                fn["rt"] = ret

        pins = []
        for p in params:
            flag = 0
            if p["out"]:
                flag |= 1
            if p["bool"]:
                flag |= 2
            if p["name"] in hidden:
                flag |= 4
            pin = [p["name"], flag]
            if p["display"]:
                pin.append(p["display"])
            pins.append(pin)
        fn["p"] = pins

        cls = db.setdefault(script_name(owner), {"f": {}})
        cls["f"].setdefault(name, fn)


def source_revision(root):
    try:
        branch = subprocess.check_output(["git", "-C", root, "rev-parse", "--abbrev-ref", "HEAD"], text=True).strip()
        commit = subprocess.check_output(["git", "-C", root, "rev-parse", "HEAD"], text=True).strip()
        return f"EpicGames/UnrealEngine {branch} {commit}"
    except (OSError, subprocess.CalledProcessError):
        return "unknown"


BLUEPRINT_FLAGS = ("BlueprintCallable", "BlueprintPure", "BlueprintEvent")


def merge_dumpspace(path, db):
    """
    Adds the functions of a Dumpspace SDK dump (FunctionsInfo.json.gz) the engine headers do not declare, the game's
    own classes. A dump is taken from the running game: it has every parameter and the function flags, but no
    metadata and no out marker, so parameters are flagged 8 ("direction unknown") and the viewer decides from the
    bytecode, hidden pins follow the WorldContextObject / FLatentActionInfo conventions.
    Returns (functions added, dump version).
    """
    with gzip.open(path, "rt", encoding="utf-8") as fh:
        dump = json.load(fh)

    added = 0
    for entry in dump.get("data", []):
        for cpp_class, functions in entry.items():
            name = script_name(cpp_class)
            for function_entry in functions:
                for function, value in function_entry.items():
                    if not isinstance(value, list) or len(value) < 4 or function.startswith("_L_"):
                        continue  # mangled Verse names never show in a blueprint

                    ret, params, _, flags = value[0], value[1], value[2], value[3]
                    flags = set(flags.split("|"))
                    if not flags & set(BLUEPRINT_FLAGS):
                        continue

                    cls = db.setdefault(name, {"f": {}})
                    if function in cls["f"]:
                        continue  # the header version carries the metadata

                    is_static = "Static" in flags
                    pins, latent = [], False
                    for param in params:
                        type_info, _, param_name = param[0], param[1], param[2]
                        type_name = type_info[0]
                        flag = 8
                        if type_name == "bool" or (type_name == "unsigned char" and re.match(r"b[A-Z]", param_name)):
                            flag |= 2
                        if type_name == "FLatentActionInfo":
                            flag |= 4
                            latent = True
                        if is_static and param_name == "WorldContextObject":
                            flag |= 4
                        pins.append([param_name, flag])

                    fn = {"p": pins, "src": "d"}
                    if "BlueprintPure" in flags:
                        fn["pure"] = 1
                    if is_static:
                        fn["static"] = 1
                    if latent:
                        fn["latent"] = 1
                    if "BlueprintEvent" in flags and "Native" not in flags:
                        fn["event"] = 1
                    ret_type = ret[0] if isinstance(ret, list) and ret else "void"
                    if ret_type != "void":
                        fn["r"] = 2 if ret_type == "bool" else 1
                    cls["f"][function] = fn
                    added += 1

    return added, dump.get("updated_at")


def merge_dumpspace_classes(path, db):
    """
    Parent chain of every class of the dump (ClassesInfo.json.gz), to find the class that declares a function
    called on an object of a derived type. Returns the number of classes given a chain.
    """
    with gzip.open(path, "rt", encoding="utf-8") as fh:
        dump = json.load(fh)

    count = 0
    for entry in dump.get("data", []):
        for cpp_class, members in entry.items():
            supers, member_types = None, {}
            for member in members:
                if not isinstance(member, dict):
                    continue
                for key, value in member.items():
                    if key == "__InheritInfo":
                        supers = [script_name(parent) for parent in value if parent != "UObject"]
                    elif not key.startswith("__") and isinstance(value, list) and value and isinstance(value[0], list):
                        # object members, the type a call on them is resolved against (Mesh -> SkeletalMeshComponent)
                        type_name, kind, pointer = value[0][0], value[0][1], value[0][2]
                        if kind == "C" and pointer == "*":
                            member_types[key.split(" : ")[0]] = script_name(type_name)

            if supers or member_types:
                cls = db.setdefault(script_name(cpp_class), {"f": {}})
                if supers:
                    cls["s"] = supers
                    count += 1
                if member_types:
                    cls["m"] = member_types

    return count


def main():
    import argparse

    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("engine", help="Unreal Engine checkout (headers)")
    parser.add_argument("output", nargs="?", default=os.path.join(os.path.dirname(__file__), "..", "FModel", "Resources", "BlueprintNodeDatabase.json.gz"))
    parser.add_argument("--dumpspace", help="Dumpspace FunctionsInfo.json.gz of the game, for its own classes")
    parser.add_argument("--dumpspace-classes", help="Dumpspace ClassesInfo.json.gz of the game, for the class hierarchy")
    args = parser.parse_args()

    db, structs = {}, {}
    count = 0
    for base in ("Engine/Source/Runtime", "Engine/Plugins"):
        for dirpath, _, files in os.walk(os.path.join(args.engine, base)):
            for f in files:
                if f.endswith(".h"):
                    parse_header(os.path.join(dirpath, f), db, structs)
                    count += 1

    source = source_revision(args.engine)
    engine_functions = sum(len(v["f"]) for v in db.values())
    dumped = 0
    if args.dumpspace:
        dumped, updated_at = merge_dumpspace(args.dumpspace, db)
        source += f" + Dumpspace {updated_at}"
    if args.dumpspace_classes:
        print(f"{merge_dumpspace_classes(args.dumpspace_classes, db)} class hierarchies from the dump")

    db = {k: v for k, v in db.items() if v.get("f") or "d" in v or "s" in v}
    structs = {k: v for k, v in structs.items() if v}
    payload = {"source": source, "classes": db, "structs": structs}
    with gzip.open(args.output, "wt", encoding="utf-8") as fh:
        json.dump(payload, fh, separators=(",", ":"), ensure_ascii=False, sort_keys=True)
    print(f"{count} headers, {len(db)} classes, {engine_functions} functions from headers + {dumped} from the dump, "
          f"{len(structs)} structs -> {args.output} ({os.path.getsize(args.output)} bytes)")


if __name__ == "__main__":
    main()
