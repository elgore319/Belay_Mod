import json, base64, numpy as np, sys
from PIL import Image
d=json.load(open('out/items.json'))
textures=[]; texidx={}
def tex(m):
    key=json.dumps(m)
    if key in texidx: return texidx[key]
    if isinstance(m,str):
        im=Image.open('out/tex_'+m+'.png').convert('RGBA')
        if im.width>256: im=im.resize((256,int(256*im.height/im.width)),Image.NEAREST)
    else:
        c=m or [0.7,0.7,0.7]; im=Image.new('RGBA',(4,4),tuple(int(255*min(1,x)) for x in c)+(255,))
    im=im.transpose(Image.FLIP_TOP_BOTTOM)  # rows bottom-up like the preview's DataTexture
    textures.append({'w':im.width,'h':im.height,'data':base64.b64encode(im.tobytes()).decode()})
    texidx[key]=len(textures)-1; return texidx[key]
items={}
for name,parts in d.items():
    out=[]
    for p in parts:
        if p.get('layer')==26 or 'v' not in p: continue
        if name=='Item_Piton' and 'Enthusiast' in p['name']: continue
        M=np.array(p['M']); v=np.array(p['v']); v[:,0]*=-1
        vh=np.c_[v,np.ones(len(v))]; w=(M@vh.T).T[:,:3]
        vt=p['vt']
        subs=p['sub'][1:] if len(p['sub'])>len(p['mats']) and not p['sub'][0] else p['sub']
        for si,faces in enumerate(subs):
            mat=p['mats'][si] if si<len(p['mats']) else None
            ti=tex(mat); tris=[]
            for f in faces:
                for k in range(1,len(f)-1):
                    tri=[f[0],f[k+1],f[k]]  # undo the exporter's winding flip
                    for vi,ui in tri:
                        tris+= [round(float(x),5) for x in w[vi]] + ([round(vt[ui][0],4),round(vt[ui][1],4)] if ui is not None and ui<len(vt) else [0,0])
            out.append({'tex':ti,'tris':tris})
    items[name]=out
json.dump({'textures':textures,'items':items},open('items_preview.json','w'))
print(len(textures), {k:sum(len(s['tris'])//15 for s in v) for k,v in items.items()})
