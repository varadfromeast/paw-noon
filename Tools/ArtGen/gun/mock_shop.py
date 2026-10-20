# python3 Tools/ArtGen/gun/mock_shop.py [locked|equipped]  ->  Tools/ArtGen/gun/shop_<state>.png
# Mock of ShopScreen.Layout at 1x (same numbers as the C#), using the real sprites and fonts.
import json, sys
from PIL import Image
A='/home/claude/wt-m7/Assets/PawNoon/Resources/Art/'
UI=json.load(open(A+'UI/ui.json'))
def img(n): return Image.open(A+'UI/%s.png'%n).convert('RGBA')
class Font:
    def __init__(s,n):
        s.j=json.load(open(A+'Fonts/%s.json'%n)); s.im=Image.open(A+'Fonts/%s.png'%n).convert('RGBA'); s.lh=s.j['lineHeight']
    def width(s,t):
        pen=0
        for c in t:
            g=s.j['glyphs'].get(c) or s.j['glyphs'].get(c.upper()) or s.j['glyphs'][' ']
            pen+=g['adv']
        return max(0,pen-1)
    def draw(s,dst,t,x,y,fill,outline=(26,16,32,255),shadow=(45,27,46,255),align='c',plain=False):
        w=s.width(t); sx = x-w//2 if align=='c' else x
        layers=[] if plain else [((-1,2),shadow),((0,2),shadow),((1,2),shadow),((-1,0),outline),((1,0),outline),((0,-1),outline),((0,1),outline)]
        layers.append(((0,0),fill))
        for (dx,dy),col in layers:
            pen=sx
            for c in t:
                g=s.j['glyphs'].get(c) or s.j['glyphs'].get(c.upper()) or s.j['glyphs'][' ']
                if g['w']>0:
                    gl=s.im.crop((g['x'],g['y'],g['x']+g['w'],g['y']+g['h']))
                    a=gl.split()[3]; solid=Image.new('RGBA',gl.size,col)
                    dst.paste(solid,(pen+g['ox']+dx,y+g['oy']+dy),a)
                pen+=g['adv']
        return w
def slice9(name,w,h):
    s=img(name); b=UI[name]['border']; l,t,r,bt=b[0],b[1],b[2],b[3]  # assume l,b?,r,t order irrelevant (symmetric)
    out=Image.new('RGBA',(w,h)); W,H=s.size
    xs=[(0,l,0,l),(l,W-r,l,w-r),(W-r,W,w-r,w)]; ys=[(0,t,0,t),(t,H-bt,t,h-bt),(H-bt,H,h-bt,h)]
    for sx0,sx1,dx0,dx1 in xs:
        for sy0,sy1,dy0,dy1 in ys:
            if dx1>dx0 and dy1>dy0:
                out.paste(s.crop((sx0,sy0,sx1,sy1)).resize((dx1-dx0,dy1-dy0),Image.NEAREST),(dx0,dy0))
    return out
M=Font('font_medium'); S=Font('font_small')
INK=(26,16,32,255)
state=sys.argv[1] if len(sys.argv)>1 else 'locked'
VW,VH=300,420
bg=Image.open('/home/claude/wt-m7/Docs/pixel-lobby.png').convert('RGBA')
bg=bg.resize((VW,int(bg.height*VW/bg.width)),Image.NEAREST).crop((0,0,VW,VH))
canvas=bg.copy(); dim=Image.new('RGBA',(VW,VH),(26,16,32,160)); canvas.alpha_composite(dim)
safe=(0,20,VW,VH-40)
PanelW,CardH,BtnW,BtnH,RW,RH=220,132,150,30,168,24
h=8+30+6+CardH+6+S.lh+6+RH+6+S.lh+10; w=PanelW
x=VW//2-w//2; y=max(safe[1]+4, safe[1]+(safe[3]-safe[1]-h)//2)
canvas.alpha_composite(slice9('panel_wood',w,h),(x,y))
cx=x+w//2; cy=y+8
canvas.alpha_composite(img('btn_icon'),(x+6,cy)); ic=img('ico_back'); canvas.alpha_composite(ic,(x+6+9,cy+9-1))
M.draw(canvas,'GENERAL STORE',cx+8,cy+(30-M.lh)//2+1,(255,235,153,255))
cy+=36
cw=w-20; canvas.alpha_composite(slice9('panel_paper',cw,CardH),(x+10,cy)); ccx=x+10+cw//2; ry=cy+8
M.draw(canvas,'THE PEACEMAKER',ccx,ry,INK,plain=True); ry+=M.lh+3
g=img('peacemaker_card'); canvas.alpha_composite(g,(ccx-g.width//2,ry)); ry+=g.height+3
S.draw(canvas,'Fast draws hit harder: 48 at 150 ms.',ccx,ry,INK,plain=True); ry+=S.lh+1
S.draw(canvas,'Blue fire. Ivory grip. Solo + casual.',ccx,ry,(110,68,40,255),plain=True); ry+=S.lh+4
py=ry; ry+=M.lh+3
if state=='locked':
    pw=7+M.width('299'); px=ccx-pw//2
    canvas.alpha_composite(img('glyph_rupee'),(px-1,py-1)); M.draw(canvas,'299',px+7,py,(255,242,168,255),align='l'); lab='BUY'; btn='btn_large'
else:
    M.draw(canvas,'OWNED',ccx,py,(107,219,94,255)); lab='EQUIPPED' if state=='equipped' else 'EQUIP'; btn='btn_large_alt' if state=='equipped' else 'btn_large'
bw=min(BtnW,cw-16); by=min(ry, cy+CardH-BtnH-6)
canvas.alpha_composite(slice9(btn,bw,BtnH),(ccx-bw//2,by)); M.draw(canvas,lab,ccx,by+BtnH//2-1-M.lh//2,(255,248,234,255))
cy+=CardH+6
S.draw(canvas,'THANK YOU, PARTNER' if state!='locked' else '',cx,cy,(251,227,174,255)); cy+=S.lh+6
rw=min(RW,w-24); canvas.alpha_composite(slice9('panel_wood',rw,RH),(cx-rw//2,cy)); M.draw(canvas,'RESTORE PURCHASES',cx,cy+RH//2-M.lh//2,(255,248,234,255)); cy+=RH+6
S.draw(canvas,'Ranked uses standard guns.',cx,cy,(241,226,198,255))
# shop button preview top-left
sb=slice9('panel_wood',62,24); canvas.alpha_composite(sb,(4,2))
lw=M.width('SHOP'); tw=lw+14; lx=4+31-tw//2
M.draw(canvas,'SHOP',lx,2+12-1-M.lh//2,(255,248,234,255),align='l'); canvas.alpha_composite(img('ico_shop'),(lx+lw+4,2+12-6-1))
canvas.resize((VW*3,VH*3),Image.NEAREST).save(__import__('os').path.join(__import__('os').path.dirname(__import__('os').path.abspath(__file__)),'shop_%s.png'%state))
print(h, 'card bottom', y+8+36+CardH, 'button bottom', by+BtnH)
