import glob,os,re
import numpy as np
from PIL import Image
for tag in ['A-default','B-exact','C-exact-legacy']:
    fs=sorted(glob.glob(f'zoom/{tag}-script/*.png'))
    rows=[]
    prev=None
    for f in fs:
        t=int(re.search(r'_(\d+)\.png',f).group(1))
        a=np.asarray(Image.open(f).convert('L'),dtype=np.float32)
        lap=a[1:-1,1:-1]*4-a[:-2,1:-1]-a[2:,1:-1]-a[1:-1,:-2]-a[1:-1,2:]
        sharp=float(lap.var())
        white=float((a>245).mean())
        diff=0.0 if prev is None else float(np.abs(a-prev).mean())
        prev=a
        rows.append((t,sharp,white,diff))
    # freezes: runs of identical frames while input active (t<2800)
    act=[r for r in rows if 300<r[0]<2800]
    frozen=sum(1 for r in act if r[3]<0.01)
    maxrun=0;run=0;start=0
    for r in act:
        if r[3]<0.01: run+=1; maxrun=max(maxrun,run)
        else: run=0
    # time for sharpness to settle after last input: first t>=2800 where sharp>=0.95*final
    final=np.mean([r[1] for r in rows[-20:]])
    settle=next((r[0] for r in rows if r[0]>2800 and r[1]>=0.95*final),None)
    # blurry frames after input end: sharp<0.7*final
    blurry=sum(1 for r in rows if r[0]>2800 and r[1]<0.7*final)
    # white (blank) frames during active
    blank=sum(1 for r in act if r[2]>0.97)
    # distinct change count during active = smoothness
    changes=len(act)-frozen
    print(f'{tag}: frames={len(rows)} active={len(act)} changing={changes} frozen={frozen} maxFrozenRun={maxrun} blank={blank} finalSharp={final:.0f} settle@{settle}ms blurryAfterInput={blurry}')
    # sharpness trace sampled
    print('  sharp trace:',' '.join(f'{r[0]}:{r[1]:.0f}' for r in rows[::25]))
