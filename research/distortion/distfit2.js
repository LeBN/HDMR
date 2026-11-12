// distfit2.js <dump.png> <L|R>
// Fit a radial distortion (center C, coeffs k1,k2) + source half-extents (A,B)
// to the CLEAN outer edges (left+top+bottom for L half; right+top+bottom for R)
// of one eye's content region. Emit params + overlay + a corrected preview.
const Jimp = require('jimp');
const [,, inp, HALF='L'] = process.argv;
const H2 = HALF.toUpperCase();

Jimp.read(inp).then(async img => {
  const W = img.bitmap.width, Ht = img.bitmap.height;
  const x0 = H2==='L' ? 0 : Math.floor(W/2);
  const hw = Math.floor(W/2), hh = Ht;
  const d = img.bitmap.data;
  const lum = (x,y) => { const i=((y)*W + (x0+x))*4; return 0.299*d[i]+0.587*d[i+1]+0.114*d[i+2]; };
  let mx=0; for(let y=0;y<hh;y+=2) for(let x=0;x<hw;x+=2) mx=Math.max(mx,lum(x,y));
  const thr = Math.max(10, mx*0.14);

  // ---- measure clean edges ----
  const inner = H2==='L';            // for L, the seam is the RIGHT edge (dirty); for R, seam is LEFT
  const meas = { left:[], right:[], top:[], bottom:[] };
  for (let yi=Math.round(hh*0.22); yi<=hh*0.78; yi+=2) {
    let L=-1,R=-1;
    for (let x=0;x<hw;x++){ if (lum(x,yi)>thr){ L=x; break; } }
    for (let x=hw-1;x>=0;x--){ if (lum(x,yi)>thr){ R=x; break; } }
    if (inner) { if (L>=0 && L<hw*0.4) meas.left.push([L,yi]); }
    else       { if (R>=0 && R>hw*0.6) meas.right.push([R,yi]); }
  }
  const cLo = H2==='L' ? 0.06 : 0.28, cHi = H2==='L' ? 0.72 : 0.94;
  for (let xi=Math.round(hw*cLo); xi<=hw*cHi; xi+=2) {
    let T=-1,B=-1;
    for (let y=0;y<hh;y++){ if (lum(xi,y)>thr){ T=y; break; } }
    for (let y=hh-1;y>=0;y--){ if (lum(xi,y)>thr){ B=y; break; } }
    if (T>=0 && T<hh*0.45) meas.top.push([xi,T]);
    if (B>=0 && B>hh*0.55) meas.bottom.push([xi,B]);
  }

  const asp = hw/hh;
  // normalize: nx in [-asp,asp], ny in [-1,1]
  const NX = px => (px/hw*2 - 1)*asp,  NY = py => (py/hh*2 - 1);
  const PX = nx => (nx/asp + 1)/2*hw,  PY = ny => (ny + 1)/2*hh;

  // forward model: source s -> displayed
  function fwd(sx, sy, P) {
    const dx = sx - P.cx, dy = sy - P.cy;
    const rr = dx*dx + dy*dy;
    const f = 1 + P.k1*rr + P.k2*rr*rr;
    return [P.cx + dx*f, P.cy + dy*f];
  }
  // for a target displayed point on a given source edge, find the sy (or sx) that lands there
  function costFor(P) {
    let e=0,n=0;
    const A=P.A, B=P.B;
    // left/right edge: sx = ∓A, vary sy; match measured (x,y)
    const sEdge = inner ? -A : A, arr = inner ? meas.left : meas.right;
    for (const [mx0,my0] of arr) {
      const tx=NX(mx0), ty=NY(my0);
      // binary search sy in [-B,B] minimizing dist to (tx,ty) along the mapped edge
      let lo=-B, hi=B, bestd=1e9;
      for (let it=0; it<28; it++){
        const m1=lo+(hi-lo)/3, m2=hi-(hi-lo)/3;
        const d1=dist(fwd(sEdge,m1,P),[tx,ty]), d2=dist(fwd(sEdge,m2,P),[tx,ty]);
        if (d1<d2) hi=m2; else lo=m1;
        bestd=Math.min(bestd,d1,d2);
      }
      e+=bestd; n++;
    }
    for (const edge of [['top',-B],['bottom',B]]) {
      for (const [mx0,my0] of meas[edge[0]]) {
        const tx=NX(mx0), ty=NY(my0);
        let lo=-A, hi=A, bestd=1e9;
        for (let it=0; it<28; it++){
          const m1=lo+(hi-lo)/3, m2=hi-(hi-lo)/3;
          const d1=dist(fwd(m1,edge[1],P),[tx,ty]), d2=dist(fwd(m2,edge[1],P),[tx,ty]);
          if (d1<d2) hi=m2; else lo=m1;
          bestd=Math.min(bestd,d1,d2);
        }
        e+=bestd; n++;
      }
    }
    return n? e/n : 1e9;
  }
  const dist=(a,b)=>Math.hypot(a[0]-b[0],a[1]-b[1]);

  // search cx,cy,k1,k2,A,B
  let P = {cx:0, cy:0, k1:0, k2:0, A:0.75, B:0.85};
  let best = {...P, c: costFor(P)};
  let step = {cx:0.25, cy:0.2, k1:0.3, k2:0.15, A:0.15, B:0.15};
  for (let round=0; round<14; round++){
    let improved=true;
    while(improved){
      improved=false;
      for (const key of ['cx','cy','k1','k2','A','B']){
        for (const dd of [-step[key], step[key]]){
          const t={...best}; t[key]+=dd;
          if (t.A<0.2||t.A>1.2||t.B<0.2||t.B>1.2) continue;
          const c=costFor(t);
          if (c < best.c - 1e-4){ best={...t,c}; improved=true; }
        }
      }
    }
    for (const k in step) step[k]*=0.62;
  }
  P = best;
  console.log(`half=${H2} size=${hw}x${hh} asp=${asp.toFixed(3)} thr=${thr.toFixed(0)}`);
  console.log(`measured pts: L/R=${(inner?meas.left:meas.right).length} top=${meas.top.length} bot=${meas.bottom.length}`);
  console.log(`FIT center=(${P.cx.toFixed(3)},${P.cy.toFixed(3)}) k1=${P.k1.toFixed(4)} k2=${P.k2.toFixed(4)} A=${P.A.toFixed(3)} B=${P.B.toFixed(3)} resid=${P.c.toFixed(3)} (norm units, *${(hh/2).toFixed(0)}=px)`);
  const cxF=(P.cx/asp+1)/2, cyF=(P.cy+1)/2;
  console.log(`center as fraction of half: x=${cxF.toFixed(3)} y=${cyF.toFixed(3)}  decenterX(from mid)=${(cxF-0.5).toFixed(3)}  ${H2==='L'?'(+ = toward seam)':'(- = toward seam)'}`);
  console.log(`>>> shader inverse: _K1 ~ ${(-P.k1).toFixed(3)}  _K2 ~ ${(-P.k2).toFixed(3)}`);

  // overlay: measured (red) + fitted edges (green)
  const ov = img.clone();
  const put=(x,y,r,g,b)=>{x=Math.round(x0+x);y=Math.round(y);if(x<0||x>=W||y<0||y>=Ht)return;const i=(y*W+x)*4;ov.bitmap.data[i]=r;ov.bitmap.data[i+1]=g;ov.bitmap.data[i+2]=b;ov.bitmap.data[i+3]=255;};
  for (const arr of [meas.left,meas.right,meas.top,meas.bottom]) for(const [x,y] of arr) for(let t=-1;t<=1;t++){put(x+t,y,255,50,50);put(x,y+t,255,50,50);}
  const N=300;
  for (let i=0;i<=N;i++){
    const s=-P.B+2*P.B*i/N; let [u,v]=fwd(inner?-P.A:P.A, s, P); put(PX(u),PY(v),40,255,90);
    const sx=-P.A+2*P.A*i/N;
    let a=fwd(sx,-P.B,P); put(PX(a[0]),PY(a[1]),40,255,90);
    let b=fwd(sx, P.B,P); put(PX(b[0]),PY(b[1]),40,255,90);
  }
  await ov.writeAsync(inp.replace(/\.png$/,'')+`-fit2${H2}.png`);
  console.log('overlay ->', inp.replace(/\.png$/,'')+`-fit2${H2}.png`);
}).catch(e=>{console.error(e);process.exit(1);});
