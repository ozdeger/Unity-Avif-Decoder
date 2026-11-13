# Wraps a Section 5 AV1 OBU stream (one key frame) in a minimal AVIF file. Usage: mux.py in.obu out.avif width height
# [depth [operating point]]; an operating point adds an a1op property that selects it
import struct,sys
def box(t,payload): return struct.pack(">I",8+len(payload))+t.encode()+payload
def fbox(t,v,f,payload): return box(t,bytes([v])+f.to_bytes(3,"big")+payload)
def leb128(data,pos):
    v=0
    for i in range(8):
        b=data[pos+i]; v|=(b&0x7f)<<(7*i)
        if not b&0x80: return v,pos+i+1
def obus(data):
    pos=0
    while pos<len(data):
        h=data[pos]; t=(h>>3)&15; ext=(h>>2)&1; hs=(h>>1)&1; p=pos+1+ext
        size,p=leb128(data,p)
        yield t,data[pos:p+size],data[p:p+size]; pos=p+size
src,dst,w,h=sys.argv[1],sys.argv[2],int(sys.argv[3]),int(sys.argv[4])
depth=int(sys.argv[5]) if len(sys.argv)>5 else 8
op=int(sys.argv[6]) if len(sys.argv)>6 else None
data=open(src,"rb").read()
seq=[o for t,o,p in obus(data) if t==1][0]
seqp=[p for t,o,p in obus(data) if t==1][0]
profile=seqp[0]>>5
payload=b"".join(o for t,o,p in obus(data) if t!=2)  # drop temporal delimiters
level=0x1f&(seqp[1]>>0)  # approximate; decoders do not check
av1c=bytes([0x81,(profile<<5)|8,0x0c|(0x40 if depth>8 else 0)|(0x20 if depth==12 else 0)])+bytes([0])+seq
ftyp=box("ftyp",b"avif"+struct.pack(">I",0)+b"avifmif1miaf")
hdlr=fbox("hdlr",0,0,b"\0"*4+b"pict"+b"\0"*12+b"\0")
pitm=fbox("pitm",0,0,struct.pack(">H",1))
infe=fbox("infe",2,0,struct.pack(">HH",1,0)+b"av01"+b"\0")
iinf=fbox("iinf",0,0,struct.pack(">H",1)+infe)
ispe=fbox("ispe",0,0,struct.pack(">II",w,h))
pixi=fbox("pixi",0,0,bytes([3,depth,depth,depth]))
a1op=box("a1op",bytes([op])) if op is not None else b""
ipco=box("ipco",ispe+pixi+box("av1C",av1c)+a1op)
assoc=bytes([0x01,0x02,0x83])+(bytes([0x84]) if a1op else b"")
ipma=fbox("ipma",0,0,struct.pack(">IHB",1,1,len(assoc))+assoc)
iprp=box("iprp",ipco+ipma)
def build(off):
    iloc=fbox("iloc",0,0,bytes([0x44,0x00])+struct.pack(">HHHH",1,1,0,1)+struct.pack(">II",off,len(payload)))
    return box("meta",b"\0\0\0\0"+hdlr+pitm+iloc+iinf+iprp)
meta=build(0); off=len(ftyp)+len(meta)+8; meta=build(off)
open(dst,"wb").write(ftyp+meta+box("mdat",payload))
