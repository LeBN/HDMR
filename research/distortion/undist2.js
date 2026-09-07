// undist2.js <dump.png> <L|R> <k1> <k2> <fillY> [fillX=1] [cxF=.5] [cyF=.5]
// matches Hdmr/NuvcStereo sampleEye. writes corrected half + brightened view.
const Jimp=require('jimp');
const a=process.argv.slice(2);
const inp=a[0],H2=(a[1]||'L').toUpperCase();
const K1=+a[2]||0,K2=+a[3]||0,FY=+a[4]||1.8,FX=+a[5]||1,CXF=a[6]!==undefined?+a[6]:0.5,CYF=a[7]!==undefined?+a[7]:0.5;
Jimp.read(inp).then(async img=>{
  const W=img.bitmap.width,Ht=img.bitmap.height,hw=Math.floor(W/2),hh=Ht,x0=H2==='L'?0:hw;
  const d=img.bitmap.data, asp=hw/hh;
  const samp=(fx,fy)=>{ if(fx<0||fx>1||fy<0||fy>1)return[0,0,0];
    const px=fx*(hw-1),py=fy*(hh-1),x1=Math.floor(px),y1=Math.floor(py),x2=Math.min(x1+1,hw-1),y2=Math.min(y1+1,hh-1),tx=px-x1,ty=py-y1;
    const g=(xx,yy,c)=>d[(yy*W+(x0+xx))*4+c]; const o=[];
    for(let c=0;c<3;c++){const t=g(x1,y1,c)*(1-tx)+g(x2,y1,c)*tx,b=g(x1,y2,c)*(1-tx)+g(x2,y2,c)*tx;o.push(t*(1-ty)+b*ty);}
    return o; };
  const cx=(CXF-0.5)*asp, cy=CYF-0.5;
  const out=new Jimp(1280,720,0x000000ff);
  for(let oy=0;oy<720;oy++)for(let ox=0;ox<1280;ox++){
    const u=ox/1280,v=oy/720;
    let px=(u-0.5)*asp/FX, py=(v-0.5)/FY;
    let dx=(px-cx),dy=(py-cy);
    const rr=dx*dx+dy*dy, f=1+K1*rr+K2*rr*rr;
    dx*=f;dy*=f;
    const sx=cx+dx, sy=cy+dy;
    const fx=sx/asp+0.5, fy=sy+0.5;
    const [r,g,b]=samp(fx,fy);
    const i=(oy*1280+ox)*4; out.bitmap.data[i]=r;out.bitmap.data[i+1]=g;out.bitmap.data[i+2]=b;out.bitmap.data[i+3]=255;
  }
  // straightness of top/bottom/left/right edges of result
  const od=out.bitmap.data, ol=(x,y)=>{const i=(y*1280+x)*4;return .299*od[i]+.587*od[i+1]+.114*od[i+2];};
  let mx=0;for(let y=0;y<720;y+=3)for(let x=0;x<1280;x+=3)mx=Math.max(mx,ol(x,y));
  const th=mx*0.16;
  const T=[],B=[],L=[],R=[];
  for(let x=Math.round(1280*0.12);x<1280*0.88;x+=3){let t=-1,b=-1;for(let y=0;y<720;y++)if(ol(x,y)>th){t=y;break;}for(let y=719;y>=0;y--)if(ol(x,y)>th){b=y;break;}if(t>=0)T.push(t);if(b>=0)B.push(b);}
  for(let y=Math.round(720*0.15);y<720*0.85;y+=3){let l=-1,r=-1;for(let x=0;x<1280;x++)if(ol(x,y)>th){l=x;break;}for(let x=1279;x>=0;x--)if(ol(x,y)>th){r=x;break;}if(l>=0)L.push(l);if(r>=0)R.push(r);}
  const sd=A=>{if(!A.length)return 99;const m=A.reduce((s,v)=>s+v,0)/A.length;return Math.sqrt(A.reduce((s,v)=>s+(v-m)**2,0)/A.length);};
  const cov=(A,tot)=>{const m=A.reduce((s,v)=>s+v,0)/A.length; return (m/tot);};
  console.log(`${H2} k1=${K1} k2=${K2} fillY=${FY} fillX=${FX} c=(${CXF},${CYF})`);
  console.log(`  straightness (px sd, lower better): T=${sd(T).toFixed(1)} B=${sd(B).toFixed(1)} L=${sd(L).toFixed(1)} R=${sd(R).toFixed(1)}`);
  console.log(`  content mean: top y=${(T.reduce((s,v)=>s+v,0)/T.length||0).toFixed(0)} bot y=${(B.reduce((s,v)=>s+v,0)/B.length||0).toFixed(0)}  -> vfill ${(((B.reduce((s,v)=>s+v,0)/B.length)-(T.reduce((s,v)=>s+v,0)/T.length))/720*100||0).toFixed(0)}%`);
  const outp=inp.replace(/\.png$/,'')+`-u2${H2}.png`;
  await out.writeAsync(outp);
  // brightened
  const bv=out.clone(); bv.brightness(0.25).contrast(0.3);
  await bv.writeAsync(inp.replace(/\.png$/,'')+`-u2${H2}v.png`);
  console.log('  ->',outp);
}).catch(e=>{console.error(e);process.exit(1);});
