"""Large-scale FX for the v3 painted art: muzzle flashes, smoke, hit puff, dust, shell. Pixel art, no AA."""
import json, os, math, random
import numpy as np
from PIL import Image, ImageDraw
HERE=os.path.dirname(os.path.abspath(__file__))
OUT=os.path.normpath(os.path.join(HERE,'..','..','..','Assets','PawNoon','Resources','Art','FX'))
rng=random.Random(11)
INK=(26,16,32,255); WHITE=(255,255,255,255); CREAM=(255,250,215,255); Y1=(255,232,120,255); Y2=(255,190,60,255); O1=(240,120,40,255); R1=(200,60,30,255)
S0=(236,230,216,255); S1=(214,204,188,255); S2=(180,168,152,255); S3=(120,108,100,255)
D0=(246,214,160,255); D1=(222,180,120,255); D2=(190,140,88,255)
def canvas(w,h): return Image.new('RGBA',(w,h),(0,0,0,0))
def star(d,cx,cy,r_out,r_in,n,col,rot=0.0):
    pts=[]
    for i in range(n*2):
        r=r_out if i%2==0 else r_in; a=rot+math.pi*i/n
        pts.append((cx+math.cos(a)*r, cy+math.sin(a)*r))
    d.polygon(pts,fill=col)
def muzzle(cell, frames, side):
    out=[]
    for i in range(frames):
        im=canvas(cell,cell); d=ImageDraw.Draw(im); c=cell/2
        k=[1.0,0.82,0.55][i]
        R=cell*0.46*k
        if side:
            # flash pointing right, pivot at left-middle (muzzle end)
            ox=cell*0.12
            star(d,ox+R*0.55,c,R,R*0.42,7,Y2,rot=0.3*i); star(d,ox+R*0.5,c,R*0.72,R*0.3,7,Y1,rot=0.3*i+0.2); star(d,ox+R*0.45,c,R*0.42,R*0.18,6,CREAM)
            d.ellipse((ox-2,c-3,ox+4,c+3),fill=WHITE)
        else:
            star(d,c,c,R,R*0.4,8,Y2,rot=0.4*i); star(d,c,c,R*0.7,R*0.28,8,Y1,rot=0.4*i+0.25); star(d,c,c,R*0.38,R*0.16,6,CREAM); d.ellipse((c-R*0.12,c-R*0.12,c+R*0.12,c+R*0.12),fill=WHITE)
        # 1px ink rim on the outer star for pixel-art read
        a=np.asarray(im).copy(); al=a[...,3]>0
        rim=al & ~(np.roll(al,1,0)&np.roll(al,-1,0)&np.roll(al,1,1)&np.roll(al,-1,1))
        a[rim]=(O1 if i<2 else R1)
        out.append(Image.fromarray(a))
    return out
def puff(cell,frames,cols):
    out=[]
    blobs=[(rng.uniform(-0.3,0.3),rng.uniform(-0.3,0.3),rng.uniform(0.18,0.32)) for _ in range(6)]
    for i in range(frames):
        im=canvas(cell,cell); d=ImageDraw.Draw(im); c=cell/2; t=i/(frames-1)
        grow=0.55+0.75*t; rise=-cell*0.18*t
        for (bx,by,br) in blobs:
            r=cell*br*grow*(1-0.35*t); x=c+bx*cell*grow; y=c+by*cell*grow+rise
            d.ellipse((x-r,y-r,x+r,y+r),fill=cols[min(len(cols)-1,int(t*len(cols)))])
        # dither out at the end
        a=np.asarray(im).copy()
        if t>0.5:
            yy,xx=np.mgrid[0:cell,0:cell]; checker=((xx+yy)%2==0)
            fade=(t-0.5)/0.5; drop=checker if fade<0.75 else (checker|((xx%2==0)))
            a[...,3]=np.where(drop & (np.random.default_rng(i).random((cell,cell))<fade*0.7), 0, a[...,3])
        al=a[...,3]>0; rim=al & ~(np.roll(al,1,0)&np.roll(al,-1,0)&np.roll(al,1,1)&np.roll(al,-1,1))
        a[rim]=cols[-1]
        out.append(Image.fromarray(a))
    return out
def hit_puff(cell,frames):
    out=[]
    for i in range(frames):
        im=canvas(cell,cell); d=ImageDraw.Draw(im); c=cell/2; t=i/(frames-1)
        R=cell*(0.2+0.3*t)
        star(d,c,c,R,R*0.55,10,CREAM,rot=0.2*i); star(d,c,c,R*0.6,R*0.3,10,WHITE)
        for k in range(6):
            a=k*math.pi/3+0.4*i; rr=R*(1.1+0.6*t); x=c+math.cos(a)*rr; y=c+math.sin(a)*rr
            s=max(1,int(cell*0.05*(1-t))); d.rectangle((x-s,y-s,x+s,y+s),fill=Y1 if k%2 else S1)
        a=np.asarray(im).copy()
        if t>0.6:
            yy,xx=np.mgrid[0:cell,0:cell]; a[...,3]=np.where(((xx+yy)%2==0), 0, a[...,3])
        al=a[...,3]>0; rim=al & ~(np.roll(al,1,0)&np.roll(al,-1,0)&np.roll(al,1,1)&np.roll(al,-1,1)); a[rim]=INK
        out.append(Image.fromarray(a))
    return out
def strip(frames):
    w,h=frames[0].size; s=canvas(w*len(frames),h)
    for i,f in enumerate(frames): s.paste(f,(i*w,0))
    return s
os.makedirs(OUT,exist_ok=True)
fx=json.load(open(os.path.join(OUT,'fx.json')))
specs={
 'muzzle_side': (muzzle(56,3,True), [56,56], 3, 24, [7,28], False),
 'muzzle_front':(muzzle(44,3,False),[44,44], 3, 24, [22,22], False),
 'smoke_puff':  (puff(40,6,[S0,S1,S2,S3]), [40,40], 6, 12, [20,30], True),
 'dust_puff':   (puff(48,6,[D0,D1,D2]), [48,48], 6, 12, [24,44], True),
 'hit_puff':    (hit_puff(56,5), [56,56], 5, 16, [28,28], False),
}
for name,(frames,cell,n,fps,pivot,alpha) in specs.items():
    strip(frames).save(os.path.join(OUT,name+'.png'))
    e=fx.get(name,{}); e.update({'file':name,'cell':cell,'frames':n,'fps':fps,'pivot':pivot,'loop':False}); fx[name]=e
json.dump(fx,open(os.path.join(OUT,'fx.json'),'w'),indent=1)
# preview
sheet=canvas(300,140); x=0
for name in specs:
    f=specs[name][0][1 if len(specs[name][0])>1 else 0]; sheet.paste(f,(x,0)); x+=f.width+4
sheet.resize((sheet.width*3,sheet.height*3),Image.NEAREST).save(os.path.join(HERE,'preview.png')); print('ok')
