# Builds SKINS_DIR input for the preview from a White Knuckle install.
# Usage: python3 make_skins.py "<game>/White Knuckle_Data/Mods/Cosmetics/Hands" <out dir> <default hand back, 128x128 PNG>
# The default hand (HND_B1A1) is not shipped as a file; extract it from sharedassets3.assets (e.g. UnityPy)
# and pad it to its 128x128 frame. Nothing extracted is committed.
import json,os,sys
from PIL import Image
import numpy as np
H=sys.argv[1]; out=sys.argv[2]; base=sys.argv[3]
os.makedirs(out,exist_ok=True)
for f in os.listdir(out): os.remove(os.path.join(out,f))
def raw(img,name):
    a=np.array(img.convert('RGBA'))[::-1]  # bottom-up
    open(os.path.join(out,name),'wb').write(a.tobytes()); return img.size
lines=[]
for d in sorted(os.listdir(H)):
    p=os.path.join(H,d,'cosmetic-handitem-settings.json')
    if not os.path.exists(p): continue
    j=json.load(open(p))
    spr={}
    for s in j.get('swapSprites',[]):
        if s['spriteName'] in ('HND_B1A1','HND_B1C1') and s.get('hand',-1) in (-1,0) and s['spriteName'] not in spr and s['replacementSpriteNames']:
            spr[s['spriteName']]=s['replacementSpriteNames'][0]
    if spr:
        back=Image.open(os.path.join(H,d,'Sprites',spr['HND_B1A1']+'.png'))
        palm=Image.open(os.path.join(H,d,'Sprites',spr.get('HND_B1C1',spr['HND_B1A1'])+'.png'))
    else:
        back=palm=Image.open(base)
    w,h=raw(back,d+'_back.raw'); raw(palm,d+'_palm.raw')
    pal='-'
    pdir=os.path.join(H,d,'Palettes')
    ids=[x['id'].lower() for x in j.get('palettes',[])]
    files={f.lower()[:-4]:f for f in os.listdir(pdir)} if os.path.isdir(pdir) else {}
    pick=next((files[i] for i in ids if i in files), None) or (sorted(files.values())[0] if files else None)
    if pick:
        im=Image.open(os.path.join(pdir,pick)); pw=im.size[0]; raw(im,d+'_pal.raw'); pal=f'{d}_pal.raw:{pw}'
    name={'climber-tattoo-skeleton':'Skeleton Tattoo','climber-outline':'Outline','climber-prosthetic':'Prosthetic','climber-wraps-basic':'Basic Wraps'}.get(d,j.get('cosmeticName',d))
    line=[name,str(w),str(h),d+'_back.raw',d+'_palm.raw',pal]
    mat=j.get('globalMaterialBase') or ''
    ov=[g for s2 in j.get('swapSprites',[]) if s2['spriteName']=='HND_B1A1' and not s2['replacementSpriteNames'] for g in s2.get('secondaryTextures',[]) if g['name']=='_OverlayMult']
    if ov and not mat: mat='-'
    if ov:
        im=Image.open(os.path.join(H,d,'Sprites',ov[0]['secondaryTextureNames'][0]+'.png'))
        sw,sh=raw(im,d+'_sec.raw'); line+=[mat,'_OverlayMult',f'{d}_sec.raw:{sw}:{sh}']; mat=''
    if mat:
        sec='-'; secname='-'
        for g in (j.get('globalSecondary') or []) if j.get('useGlobalSecondary') else []:
            if g['name'] in ('_OverlayMult','_StaminaTexture'):
                im=Image.open(os.path.join(H,d,'Sprites',g['secondaryTextureNames'][0]+'.png'))
                sw,sh=raw(im,d+'_sec.raw'); sec=f'{d}_sec.raw:{sw}:{sh}'; secname=g['name']; break
        line+=[mat,secname,sec]
    lines.append('|'.join(line))
open(os.path.join(out,'manifest.txt'),'w').write('\n'.join(lines))
print(len(lines)); print('\n'.join(lines))
