# Makes a modular INSIDE patch ("INSIDEPATCH2"): only the Unity objects a mod replaces, so several mods can be
# installed on the same game file at once, each one on or off independently.
#   magic(12) name(str) description(str) fileCount(i32)
#   per file: relPath(str) vanillaLen(i64) vanillaSha256(32) objCount(i32)
#     per object: pathId(i64) origLen(u32) origSha256(32) newLen(u32) newBytes
#   str = i32 byte length + UTF-8
import struct, hashlib

def parse(b):
    """Unity serialized file (v14-16, no type trees) -> version, data offset, [(pathId, tablePos, relStart, size)]"""
    ms, fs, ver, do = struct.unpack('>IIII', b[:16]); assert ver in (14, 15, 16) and b[16] == 0
    p = b.index(0, 20) + 1; p += 4; assert b[p] == 0; p += 1
    n, = struct.unpack_from('<i', b, p); p += 4
    for _ in range(n):
        cid, = struct.unpack_from('<i', b, p); p += 4
        if ver >= 16: p += 3
        if cid < 0: p += 16
        p += 16
    n, = struct.unpack_from('<i', b, p); p += 4
    objs = []
    for _ in range(n):
        p = (p + 3) & ~3
        pid, bs, sz = struct.unpack_from('<qII', b, p); objs.append((pid, p + 8, bs, sz))
        p += 24 + (1 if ver >= 15 else 0)
    return ver, do, objs

def objects(b):
    ver, do, objs = parse(b)
    return {pid: b[do + bs:do + bs + sz] for pid, _, bs, sz in objs}

def s_(x): x = x.encode('utf-8'); return struct.pack('<i', len(x)) + x

def make(out, name, desc, files):
    bb = bytearray(b'INSIDEPATCH2') + s_(name) + s_(desc) + struct.pack('<i', len(files))
    for rel, orig, new in files:
        a, c = objects(orig), objects(new)
        assert set(a) == set(c), 'mods can only change existing objects'
        changed = sorted(pid for pid in a if a[pid] != c[pid])
        bb += s_(rel) + struct.pack('<q', len(orig)) + hashlib.sha256(orig).digest() + struct.pack('<i', len(changed))
        for pid in changed:
            bb += struct.pack('<qI', pid, len(a[pid])) + hashlib.sha256(a[pid]).digest() + struct.pack('<I', len(c[pid])) + c[pid]
        print(rel, 'objects', changed)
    open(out, 'wb').write(bb); print(out, len(bb), 'bytes')

def make_objs(out, name, desc, rel, orig, pids, new):
    """same, but only for the listed objects of an already patched file (used to split a mod into several)"""
    a, c = objects(orig), objects(new)
    bb = bytearray(b'INSIDEPATCH2') + s_(name) + s_(desc) + struct.pack('<i', 1)
    bb += s_(rel) + struct.pack('<q', len(orig)) + hashlib.sha256(orig).digest() + struct.pack('<i', len(pids))
    for pid in sorted(pids):
        bb += struct.pack('<qI', pid, len(a[pid])) + hashlib.sha256(a[pid]).digest() + struct.pack('<I', len(c[pid])) + c[pid]
    open(out, 'wb').write(bb); print(out, len(bb), 'bytes')

if __name__ == '__main__':
    D = '/mnt/user-data/uploads/INSIDE/INSIDE_Data'
    make('TestSubjectA_WindowMoment.insidepatch', 'Test subject A: the unused window moment',
         'Education Rooms: when the boy walks up to the test chamber, test subject A plays his unused 19 s moment '
         '(Gen_HuddleSurgeryA_Moment) and the chamber curtains follow their animation again. Plays once per level '
         'load or respawn.',
         [('INSIDE_Data/level57', open(D + '/level57', 'rb').read(), open('level57.patched', 'rb').read())])
