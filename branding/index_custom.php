<?php
/* AFIF-PBX landing page (FreePBX loads index_custom.php from the web root when it exists). */
$host = htmlspecialchars($_SERVER['HTTP_HOST'] ?? 'localhost', ENT_QUOTES);
$ucp  = is_dir(__DIR__ . '/ucp');
?><!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>AFIF-PBX</title>
<link rel="icon" href="/admin/assets/afif/favicon.ico">
<link rel="stylesheet" href="/admin/assets/afif/landing.css">
</head>
<body>
<canvas id="bg" aria-hidden="true"></canvas>
<main class="stage">
  <div class="emblem">
    <div class="halo"></div>
    <div class="orbit o1"></div><div class="orbit o2"></div>
    <img class="logo" src="/admin/assets/afif/logo.png" alt="AFIF-PBX">
    <div class="sweep"></div>
  </div>
  <p class="tagline">Business telephony &middot; GSM gateway &middot; by AFIF-TECH</p>
  <nav class="actions">
    <a class="btn primary" href="/admin/config.php">Administration</a>
    <?php if ($ucp): ?><a class="btn" href="/ucp/">User Control Panel</a><?php endif; ?>
  </nav>
  <footer>AFIF-PBX on <?php echo $host; ?></footer>
</main>
<script>
/* Animated signal field behind the logo */
(function(){
  var c=document.getElementById('bg'), x=c.getContext('2d'), w,h,dpr,pts=[];
  function size(){dpr=window.devicePixelRatio||1;w=c.width=innerWidth*dpr;h=c.height=innerHeight*dpr;
    c.style.width=innerWidth+'px';c.style.height=innerHeight+'px';
    pts=[];var n=Math.round(innerWidth*innerHeight/14000);
    for(var i=0;i<n;i++)pts.push({x:Math.random()*w,y:Math.random()*h,vx:(Math.random()-.5)*.35*dpr,vy:(Math.random()-.5)*.35*dpr});}
  size(); addEventListener('resize',size);
  var reduce=matchMedia('(prefers-reduced-motion: reduce)').matches, t=0;
  function frame(){
    x.clearRect(0,0,w,h); t+=.008;
    for(var k=0;k<3;k++){ // travelling waves
      x.beginPath(); x.lineWidth=1.2*dpr; x.strokeStyle='rgba(56,225,255,'+(.10-k*.025)+')';
      for(var X=0;X<=w;X+=8*dpr){var Y=h*.72+Math.sin(X/(180*dpr)+t*(2+k)+k)*28*dpr*(1+k*.6);X?x.lineTo(X,Y):x.moveTo(X,Y);}
      x.stroke();}
    for(var i=0;i<pts.length;i++){var p=pts[i];p.x+=p.vx;p.y+=p.vy;
      if(p.x<0||p.x>w)p.vx*=-1; if(p.y<0||p.y>h)p.vy*=-1;
      x.fillStyle='rgba(127,166,217,.55)'; x.fillRect(p.x,p.y,1.6*dpr,1.6*dpr);
      for(var j=i+1;j<pts.length;j++){var q=pts[j],dx=p.x-q.x,dy=p.y-q.y,d=dx*dx+dy*dy,m=(120*dpr)*(120*dpr);
        if(d<m){x.strokeStyle='rgba(56,225,255,'+(.12*(1-d/m))+')';x.lineWidth=dpr;x.beginPath();x.moveTo(p.x,p.y);x.lineTo(q.x,q.y);x.stroke();}}}
    if(!reduce) requestAnimationFrame(frame);
  }
  frame();
})();
</script>
</body>
</html>
