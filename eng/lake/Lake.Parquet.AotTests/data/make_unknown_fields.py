# Builds lake_unknown_footer_fields.parquet: a pyarrow 26 file whose footer (FileMetaData) carries, before its stop,
# unknown fields of every Thrift compact type, as a writer on a newer parquet-format might add (lake(0067)).
# python3 make_unknown_fields.py
import struct, pyarrow as pa, pyarrow.parquet as pq

def varint(v):
    out = bytearray()
    while v >= 0x80:
        out.append((v & 0x7f) | 0x80); v >>= 7
    out.append(v); return bytes(out)

def zigzag(v): return varint((v << 1) ^ (v >> 63) if v < 0 else v << 1)
def header(fid, t): return bytes([t]) + zigzag(fid)
def binary(s): b = s.encode(); return varint(len(b)) + b

def unknown(depth=0):
    fid = 1000 + depth * 100; out = bytearray()
    def h(t):
        nonlocal fid; out.extend(header(fid, t)); fid += 1
    h(1); h(2)
    h(3); out.append(0x7f)
    h(4); out.extend(zigzag(-300))
    h(5); out.extend(zigzag(1 << 20))
    h(6); out.extend(zigzag(-(1 << 63)))
    h(7); out.extend(struct.pack('<d', 3.141592653589793))
    h(8); out.extend(binary('unknown'))
    h(9); out.append(0x35); out.extend(zigzag(1) + zigzag(2) + zigzag(3))
    h(9); out.extend(bytes([0x21, 1, 2]))
    h(9); out.append(0xf8); out.extend(varint(16)); [out.extend(binary('e%d' % i)) for i in range(16)]
    h(10); out.append(0x18); out.extend(binary('s'))
    h(11); out.extend(varint(2)); out.append(0x51); out.extend(zigzag(7) + b'\x01' + zigzag(8) + b'\x02')
    h(11); out.extend(varint(0))
    h(13); out.extend(bytes(16))
    if depth < 2:
        h(12); out.extend(unknown(depth + 1)); out.append(0)
        h(9); out.append(0x2c); out.extend(unknown(depth + 1) + b'\x00' + unknown(depth + 1) + b'\x00')
    return bytes(out)

pq.write_table(pa.table({'id': pa.array([1, 2, 3], pa.int64()), 's': ['a', None, 'c']}), '/tmp/base.parquet', compression='none')
data = open('/tmp/base.parquet', 'rb').read()
n = struct.unpack('<I', data[-8:-4])[0]
footer = data[-8 - n:-8]
assert footer[-1] == 0
footer = footer[:-1] + unknown() + b'\x00'
open('lake_unknown_footer_fields.parquet', 'wb').write(data[:-8 - n] + footer + struct.pack('<I', len(footer)) + b'PAR1')
