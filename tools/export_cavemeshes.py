# Regenerates ../cavemeshes.bin from the game's assets (needed only if a game update changes the cave rocks).
#
#   pip install --user UnityPy==1.25.3 lz4 && pip install --user --upgrade --ignore-installed attrs
#   python3 unfs.py   <game>/Apocalypter_Data/data.unity3d  WORK  sharedassets1.assets          (ModDev/Textures/_tools/unfs.py, ~92 MB)
#   python3 sparse.py <game>/Apocalypter_Data/data.unity3d  WORK  sharedassets1.assets.resS rng.json   (only the mesh byte ranges; sparse file)
#   python3 export_cavemeshes.py WORK  [--ranges]   -> first run with --ranges writes WORK/rng.json, then run sparse.py, then run again
#
# Format: "CDM1", int count, per mesh: int nameLen, name, int vertexCount, int indexCount,
#         float3 pos[vc], float3 normal[vc], float2 uv[vc], int index[ic]   (little endian, mesh-local, the game's own winding)
import UnityPy, os, struct, sys, json
from UnityPy.helpers.MeshHelper import MeshHandler

WANT = ['Rock1_Cave', 'Rock2_Cave', 'Rock3_Cave', 'Rock4_Cave', 'Rock5_Cave', 'cave_entrance', 'camp_base']
work = sys.argv[1]
env = UnityPy.load(os.path.join(work, 'sharedassets1.assets'))
meshes = [o.read() for o in env.objects if o.type.name == 'Mesh']
meshes = [m for m in meshes if m.m_Name in WANT]
if '--ranges' in sys.argv:
    json.dump([[m.m_StreamData.offset, m.m_StreamData.size] for m in meshes if m.m_StreamData.size], open(os.path.join(work, 'rng.json'), 'w'))
    sys.exit()
out = open(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'cavemeshes.bin'), 'wb')
out.write(b'CDM1'); out.write(struct.pack('<i', len(meshes)))
for m in meshes:
    h = MeshHandler(m); h.process()
    v, n, uv = h.m_Vertices, h.m_Normals, h.m_UV0
    tris = [i for sub in h.get_triangles() for tri in sub for i in tri]
    name = m.m_Name.encode()
    out.write(struct.pack('<i', len(name))); out.write(name)
    out.write(struct.pack('<ii', len(v), len(tris)))
    for x in v: out.write(struct.pack('<3f', *x[:3]))
    for x in n: out.write(struct.pack('<3f', *x[:3]))
    for x in uv: out.write(struct.pack('<2f', *x[:2]))
    for i in tris: out.write(struct.pack('<i', i))
    print(m.m_Name, len(v), len(tris) // 3)
out.close()
