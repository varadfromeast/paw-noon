"""User-generated UI pieces -> Art/UI (cue_draw, stamp_too_early, 6 grade stamps, logo, result_card)."""
import json, os, numpy as np
from PIL import Image
from scipy import ndimage
HERE=os.path.dirname(os.path.abspath(__file__)); OUT=os.path.normpath(os.path.join(HERE,'..','..','..','Assets','PawNoon','Resources','Art','UI'))
def key(a):
    r,g,b=[a[...,i].astype(int) for i in range(3)]; return ~((r>150)&(g<100)&(b>120)&(r-g>110)&(b-g>80))
def comps(m,min_px):
    lab,n=ndimage.label(ndimage.binary_dilation(m,iterations=10)); out=[]
    for i in range(1,n+1):
        ys,xs=np.where(lab==i)
        if len(ys)>min_px: out.append((xs.min(),ys.min(),xs.max()+1,ys.max()+1))
    return out
def cut(a,m,box,target_w):
    x0,y0,x1,y1=box; c=a[y0:y1,x0:x1]; mm=m[y0:y1,x0:x1]
    s=target_w/(x1-x0); nw,nh=round((x1-x0)*s),round((y1-y0)*s)
    prem=Image.fromarray((c*mm[...,None]).astype(np.uint8)).resize((nw,nh),Image.BOX)
    al=np.asarray(Image.fromarray((mm*255).astype(np.uint8)).resize((nw,nh),Image.BOX)).astype(float)/255
    col=np.asarray(prem).astype(float)/np.maximum(al[...,None],1e-3)
    o=np.zeros((nh,nw,4),np.uint8); o[...,:3]=np.clip(col,0,255); o[...,3]=(al>0.5)*255
    im=Image.fromarray(o); return im.crop(im.getbbox())
def quant(im,n=64):
    q=np.asarray(im.convert('RGB').quantize(colors=n,method=Image.Quantize.MEDIANCUT,dither=Image.Dither.NONE).convert('RGBA')).copy(); q[...,3]=np.asarray(im)[...,3]; return Image.fromarray(q)
ui=json.load(open(os.path.join(OUT,'ui.json')))
def put(name,im,pivot=None,border=None):
    im.save(os.path.join(OUT,name+'.png')); e=ui.get(name,{}); e.update({'file':name,'size':[im.width,im.height],'pivot':pivot or [im.width//2,im.height//2]})
    if border: e['border']=border
    e.pop('cell',None); e.pop('frames',None); ui[name]=e; print(name,im.size)
# cues: two rows
a=np.asarray(Image.open(os.path.join(HERE,'src_cues.png')).convert('RGB')); m=key(a)
rows=sorted(comps(m,5000),key=lambda b:b[1])
put('cue_draw',quant(cut(a,m,rows[0],212)))
put('stamp_too_early',quant(cut(a,m,rows[1],150)))
# stamps: 3x2
a=np.asarray(Image.open(os.path.join(HERE,'src_stamps.png')).convert('RGB')); m=key(a)
bx=sorted(comps(m,20000),key=lambda b:(b[1]//300,b[0]))
names=['grade_lightning','grade_perfect','grade_great','grade_good','grade_slow','grade_slowpoke']
for n,b in zip(names,bx): put(n,quant(cut(a,m,b,64)))
# logo
a=np.asarray(Image.open(os.path.join(HERE,'src_logo.png')).convert('RGB')); m=key(a); b=comps(m,50000)[0]
put('logo_v3',quant(cut(a,m,b,236),)); put('logo',quant(cut(a,m,b,236)))
# result card (painted parchment, 2:1) -> 230 wide
a=np.asarray(Image.open(os.path.join(HERE,'src_card.png')).convert('RGB')); m=key(a); b=comps(m,50000)[0]
card=quant(cut(a,m,b,230),48); put('result_card',card,pivot=[card.width//2,card.height//2])
json.dump(ui,open(os.path.join(OUT,'ui.json'),'w'),indent=1)
# contact
sheet=Image.new('RGBA',(760,300),(60,110,160,255)); x=4;y=4;rowh=0
for n in ['cue_draw','stamp_too_early','logo','result_card']+names:
    im=Image.open(os.path.join(OUT,n+'.png'))
    if x+im.width>sheet.width: x=4; y+=rowh+4; rowh=0
    sheet.alpha_composite(im,(x,y)); x+=im.width+6; rowh=max(rowh,im.height)
sheet.resize((sheet.width*2,sheet.height*2),Image.NEAREST).save(os.path.join(HERE,'preview.png'))
