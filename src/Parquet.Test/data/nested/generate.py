# Builds nested_layout.parquet (pyarrow 26) and nested_layout.json: for each leaf column, the offsets and validity
# Arrow itself holds for it after reading the file, outermost first, the oracle for NestedLayout (lake change 0079).
# python3 generate.py
import json, pyarrow as pa, pyarrow.parquet as pq

rows = 7
ll = [[[1, 2], [3]], [], None, [[None], []], [[4]], [None, [5, 6, 7]], [[]]]
mp = [[("a", [1, None]), ("b", [])], [], None, [("c", None)], [("d", [2])], [("e", [3]), ("f", [4, 5])], None]
ls = [[{"a": 1, "b": ["x"]}, None, {"a": None, "b": None}], None, [], [{"a": 2, "b": []}], [{"a": 3, "b": ["y", None, "z"]}], [None], [{"a": 4, "b": ["w"]}]]
st = [{"p": {"q": 1}}, None, {"p": None}, {"p": {"q": None}}, {"p": {"q": 5}}, None, {"p": {"q": 7}}]
req = [[1], [2, 3], [], [4], [5], [6, 7, 8], [9]]

table = pa.table({
    "ll": pa.array(ll, pa.list_(pa.list_(pa.int32()))),
    "mp": pa.array(mp, pa.map_(pa.string(), pa.list_(pa.int64()))),
    "ls": pa.array(ls, pa.list_(pa.struct([("a", pa.int32()), ("b", pa.list_(pa.string()))]))),
    "st": pa.array(st, pa.struct([("p", pa.struct([("q", pa.int32())]))])),
    "req": pa.array(req, pa.list_(pa.field("element", pa.int32(), nullable=False))),
}, schema=pa.schema([
    pa.field("ll", pa.list_(pa.list_(pa.int32()))),
    pa.field("mp", pa.map_(pa.string(), pa.list_(pa.int64()))),
    pa.field("ls", pa.list_(pa.struct([("a", pa.int32()), ("b", pa.list_(pa.string()))]))),
    pa.field("st", pa.struct([("p", pa.struct([("q", pa.int32())]))])),
    pa.field("req", pa.list_(pa.field("element", pa.int32(), nullable=False)), nullable=False),
]))
pq.write_table(table, "nested_layout.parquet", compression="none")
back = pq.read_table("nested_layout.parquet")

leaves = {}
def walk(arr, field, path, parent_valid, offsets, validity):
    n = len(arr)
    iv = arr.is_valid().to_pylist()
    valid = [bool(iv[i]) and (parent_valid is None or parent_valid[i]) for i in range(n)]
    layer = len(offsets)
    if field.nullable:
        validity = validity + [{"layer": layer, "valid": valid}]
    t = field.type
    if pa.types.is_map(t):
        offs = arr.offsets.to_pylist()
        o = offsets + [offs]
        walk(arr.keys, pa.field("key", t.key_type, nullable=False), path + ["key_value", "key"], None, o, validity)
        walk(arr.items, t.item_field, path + ["key_value", "value"], None, o, validity)
    elif pa.types.is_list(t):
        offs = arr.offsets.to_pylist()
        walk(arr.values, t.value_field, path + ["list", "element"], None, offsets + [offs], validity)
    elif pa.types.is_struct(t):
        for i, f in enumerate(t):
            walk(arr.field(i), f, path + [f.name], valid, offsets, validity)
    else:
        leaves["/".join(path)] = {"offsets": offsets, "validity": validity, "values": sum(valid) if field.nullable else n}

for f in back.schema:
    walk(back.column(f.name).combine_chunks(), f, [f.name], None, [], [])
json.dump({"rows": rows, "leaves": leaves}, open("nested_layout.json", "w"), indent=1)
print(list(leaves))
