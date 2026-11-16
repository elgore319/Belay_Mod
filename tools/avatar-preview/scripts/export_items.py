import UnityPy, sys, json, os, numpy as np, re, traceback
env=UnityPy.load(os.environ.get('BUNDLE','all.bundle'))
ITEMS=sys.argv[1].split(',')
want=set(ITEMS)
byname={}
for o in env.objects:
    if o.type.name=='GameObject':
        n=o.peek_name()
        if n in want: byname.setdefault(n,[]).append(o)
def comp(go,tname):
    for c in go.m_Component:
        p=c.component
        if p.type.name==tname: return p
    return None
def trs(tr):
    p=tr.m_LocalPosition; r=tr.m_LocalRotation; s=tr.m_LocalScale
    x,y,z,w=r.x,r.y,r.z,r.w
    R=np.array([[1-2*(y*y+z*z),2*(x*y-z*w),2*(x*z+y*w)],[2*(x*y+z*w),1-2*(x*x+z*z),2*(y*z-x*w)],[2*(x*z-y*w),2*(y*z+x*w),1-2*(x*x+y*y)]])
    M=np.eye(4); M[:3,:3]=R@np.diag([s.x,s.y,s.z]); M[:3,3]=[p.x,p.y,p.z]; return M
texdone={}
def tex_of(mat):
    try:
        m=mat.read()
        for k,v in m.m_SavedProperties.m_TexEnvs:
            if k in ('_MainTex','_BaseMap','_Albedo','_MainTexture') and v.m_Texture.path_id:
                t=v.m_Texture.read(); name=re.sub(r'\W','_',t.m_Name)
                if name not in texdone:
                    t.image.save('out/tex_'+name+'.png'); texdone[name]=1
                return name
        cols=dict(m.m_SavedProperties.m_Colors)
        c=cols.get('_Color') or cols.get('_BaseColor')
        return None if c is None else [c.r,c.g,c.b]
    except Exception as e:
        return None
result={}
for n in ITEMS:
    objs=byname.get(n,[])
    root=None
    for o in objs:
        go=o.read(); tr=comp(go,'Transform').read()
        if tr.m_Father.path_id==0: root=(go,tr); break
    if not root: print('missing',n); continue
    parts=[]
    def walk(tr,M,top):
        go=tr.m_GameObject.read()
        if not go.m_IsActive: return
        layer=go.m_Layer
        L=np.eye(4) if top else M@trs(tr)
        mf=comp(go,'MeshFilter'); mr=comp(go,'MeshRenderer'); sr=comp(go,'SpriteRenderer')
        try:
          if mf and mr:
            r=mr.read()
            if r.m_Enabled:
                mesh=mf.read().m_Mesh.read()
                data=mesh.export().splitlines()
                v=[];vt=[];faces=[];
                for line in data:
                    sp=line.split()
                    if not sp: continue
                    if sp[0]=='v': v.append([float(a) for a in sp[1:4]])
                    elif sp[0]=='vt': vt.append([float(a) for a in sp[1:3]])
                    elif sp[0]=='g': faces.append([])
                    elif sp[0]=='f':
                        if not faces: faces.append([])
                        faces[-1].append([[int(x)-1 for x in a.split('/')[:2]] for a in sp[1:]])
                mats=[tex_of(m) for m in r.m_Materials]
                parts.append({'layer':layer,'name':go.m_Name,'M':L.tolist(),'v':v,'vt':vt,'sub':faces,'mats':mats,'objflip':True})
          if sr:
            r=sr.read()
            if r.m_Enabled and r.m_Sprite.path_id:
                sp=r.m_Sprite.read(); nm=re.sub(r'\W','_',sp.m_Name)
                sp.image.save('out/spr_'+nm+'.png')
                parts.append({'layer':layer,'name':go.m_Name,'M':L.tolist(),'sprite':nm,'ppu':sp.m_PixelsToUnits,'rect':[sp.m_Rect.width,sp.m_Rect.height],'pivot':[sp.m_Pivot.x,sp.m_Pivot.y]})
        except Exception as e:
            print('part err',n,go.m_Name,e)
        for ch in tr.m_Children: walk(ch.read(),L,False)
    walk(root[1],np.eye(4),True)
    result[n]=parts
    print(n,len(parts),[p['name'] for p in parts])
json.dump(result,open('out/items.json','w'))
