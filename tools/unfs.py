import struct, sys, lz4.block, os
p=sys.argv[1]; out=sys.argv[2]; want=sys.argv[3:]  # node names to write (empty = list only)
f=open(p,'rb')
def cstr():
    b=b''
    while True:
        c=f.read(1)
        if c==b'\0': return b.decode()
        b+=c
sig=cstr(); ver=struct.unpack('>I',f.read(4))[0]; uv=cstr(); ur=cstr()
size,cbi,ubi,flags=struct.unpack('>qIII',f.read(20))
print(sig,ver,uv,ur,size,cbi,ubi,hex(flags))
hdr_end=f.tell()
if ver>=7: f.seek((f.tell()+15)//16*16)
if flags&0x80:
    pos=f.tell(); f.seek(size-cbi); bi=f.read(cbi); f.seek(pos)
else:
    bi=f.read(cbi)
ct=flags&0x3f
if ct in (2,3): bi=lz4.block.decompress(bi,uncompressed_size=ubi)
elif ct==1: import lzma; bi=lzma.decompress(bi)
o=16; n=struct.unpack('>I',bi[o:o+4])[0]; o+=4
blocks=[]
for i in range(n):
    u,c,fl=struct.unpack('>IIH',bi[o:o+10]); o+=10; blocks.append((u,c,fl))
nn=struct.unpack('>I',bi[o:o+4])[0]; o+=4
nodes=[]
for i in range(nn):
    off,sz,fl=struct.unpack('>qqI',bi[o:o+20]); o+=20
    e=bi.index(b'\0',o); name=bi[o:e].decode(); o=e+1
    nodes.append((off,sz,fl,name))
if ver>=7 and (flags&0x200): f.seek((f.tell()+15)//16*16)
data_start=f.tell()
with open(os.path.join(out,'nodes.txt'),'w') as nf:
    for nd in nodes: nf.write('%d %d %d %s\n'%nd); print(nd)
if not want: sys.exit()
# stream blocks, writing wanted node ranges
want=set(want); outs={}
for off,sz,fl,name in nodes:
    if name in want: outs[name]=(off,sz,open(os.path.join(out,name),'wb'))
cur=0
for u,c,fl in blocks:
    d=f.read(c); t=fl&0x3f
    if t in (2,3): d=lz4.block.decompress(d,uncompressed_size=u)
    elif t==1:
        import lzma; d=lzma.decompress(d)
    for name,(off,sz,fh) in outs.items():
        a=max(off,cur); b=min(off+sz,cur+u)
        if a<b: fh.write(d[a-cur:b-cur])
    cur+=u
for name,(off,sz,fh) in outs.items(): fh.close(); print('wrote',name,sz)
