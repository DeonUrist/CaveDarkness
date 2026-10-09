import struct,sys,lz4.block,os,json
p=sys.argv[1]; out=sys.argv[2]; node=sys.argv[3]; rng=json.load(open(sys.argv[4]))
f=open(p,'rb')
def cstr():
    b=b''
    while True:
        c=f.read(1)
        if c==b'\0': return b.decode()
        b+=c
sig=cstr(); ver=struct.unpack('>I',f.read(4))[0]; uv=cstr(); ur=cstr()
size,cbi,ubi,flags=struct.unpack('>qIII',f.read(20))
if ver>=7: f.seek((f.tell()+15)//16*16)
if flags&0x80:
    pos=f.tell(); f.seek(size-cbi); bi=f.read(cbi); f.seek(pos)
else: bi=f.read(cbi)
ct=flags&0x3f
if ct in (2,3): bi=lz4.block.decompress(bi,uncompressed_size=ubi)
o=16; n=struct.unpack('>I',bi[o:o+4])[0]; o+=4
blocks=[]
for i in range(n):
    u,c,fl=struct.unpack('>IIH',bi[o:o+10]); o+=10; blocks.append((u,c,fl))
nn=struct.unpack('>I',bi[o:o+4])[0]; o+=4
nodes={}
for i in range(nn):
    off,sz,fl=struct.unpack('>qqI',bi[o:o+20]); o+=20
    e=bi.index(b'\0',o); name=bi[o:e].decode(); o=e+1; nodes[name]=(off,sz)
if ver>=7 and (flags&0x200): f.seek((f.tell()+15)//16*16)
noff,nsz=nodes[node]
want=[(noff+a,noff+a+s) for a,s in rng]
fh=open(os.path.join(out,node),'wb'); fh.truncate(nsz)
cur=0; wrote=0
for u,c,fl in blocks:
    hit=[(a,b) for a,b in want if a<cur+u and b>cur]
    if not hit: f.seek(c,1); cur+=u; continue
    d=f.read(c); t=fl&0x3f
    if t in (2,3): d=lz4.block.decompress(d,uncompressed_size=u)
    elif t==1:
        import lzma; d=lzma.decompress(d)
    for a,b in hit:
        x=max(a,cur); y=min(b,cur+u); fh.seek(x-noff); fh.write(d[x-cur:y-cur]); wrote+=y-x
    cur+=u
fh.close(); print('wrote',wrote)
