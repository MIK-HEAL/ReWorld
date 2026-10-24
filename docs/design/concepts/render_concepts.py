from PIL import Image, ImageDraw, ImageFont, ImageFilter
from pathlib import Path
import math, random
OUT=Path(__file__).resolve().parent
S=2
W,H=1280,720
FONT='C:/Windows/Fonts/msyh.ttc'
BOLD='C:/Windows/Fonts/msyhbd.ttc'
def font(n,b=False): return ImageFont.truetype(BOLD if b else FONT,round(n*S))
def begin(bg):
    global im,d
    im=Image.new('RGB',(W*S,H*S),bg);d=ImageDraw.Draw(im)
def pxy(p):return tuple(round(v*S) for v in p)
def rect(box,fill,outline=None,width=1):d.rectangle(pxy(box),fill,outline,width*S)
def line(points,fill,width=1):d.line([pxy(p) for p in points],fill,width=width*S)
def poly(points,fill,outline=None,width=1):
    pts=[pxy(p) for p in points];d.polygon(pts,fill)
    if outline:d.line(pts+[pts[0]],outline,width=width*S)
def ellipse(box,fill=None,outline=None,width=1):d.ellipse(pxy(box),fill,outline,width*S)
def text(x,y,t,n=14,c='#c9d4da',b=False):d.text(pxy((x,y)),t,font=font(n,b),fill=c)
def panel(x,y,w,h,c='#202b31'):
    poly([(x,y),(x+w-9,y),(x+w,y+9),(x+w,y+h),(x,y+h)],c)
    line([(x,y),(x+34,y)],'#da9a4e',2)
def corner(x,y,w,h,c='#dfa24f'):
    for a,b,cc in [((x,y+12),(x,y),(x+12,y)),((x+w-12,y),(x+w,y),(x+w,y+12)),((x,y+h-12),(x,y+h),(x+12,y+h)),((x+w-12,y+h),(x+w,y+h),(x+w,y+h-12))]:
        line([a,b,cc],c,2)
def key(x,y,t):
    rect((x,y,x+26,y+22),'#26363e','#63737c');text(x+6,y+1,t,12,'#dde6eb')
def arrow(a,b,c,width=2):
    line([a,b],c,width); ang=math.atan2(b[1]-a[1],b[0]-a[0])
    poly([b,(b[0]-9*math.cos(ang-.5),b[1]-9*math.sin(ang-.5)),(b[0]-9*math.cos(ang+.5),b[1]-9*math.sin(ang+.5))],c)
def top_resources(dark=True):
    panel(774,18,482,36,'#202b31' if dark else '#35434b')
    for x,label,num,c in [(790,'铁质','128','#b7c2cb'),(903,'合金','46','#e3c497'),(1010,'燃料','18','#a9c9bc'),(1125,'晶体','6','#a7c6e0')]:
        text(x,26,label,12,c);text(x+38,23,num,17,'#eff3ef',True)
def save(name):
    im.resize((W,H),Image.Resampling.LANCZOS).save(OUT/name)
def module(x,y,u,kind='hull',active=False):
    colors={'hull':'#b5c2c9','core':'#e1d9b8','reactor':'#d1a66b','radiator':'#8db9bb','thruster':'#778f9f','weapon':'#9caab4','cargo':'#b7a48b','solar':'#b5c3d4','miner':'#9ba891','refinery':'#b99578'}
    c=colors.get(kind,'#b5c2c9');k=u*.1
    pts=[(x+k,y),(x+u-k,y),(x+u,y+k),(x+u,y+u-k),(x+u-k,y+u),(x+k,y+u),(x,y+u-k),(x,y+k)]
    poly([(a+3,b+4) for a,b in pts],'#7b898e')
    poly(pts,c,'#485a64',1)
    line([(x+k,y+2),(x+u-k,y+2)],'#e1e6e5',1)
    if kind=='hull':
        line([(x+k,y+u*.7),(x+u*.7,y+k)],'#8296a1',1)
        line([(x+k,y+u*.8),(x+u*.8,y+k)],'#cad4d8',1)
    elif kind=='core':
        a=u*.23;poly([(x+u/2,y+a),(x+u-a,y+u/2),(x+u/2,y+u-a),(x+a,y+u/2)],'#465d68')
        ellipse((x+u*.4,y+u*.4,x+u*.6,y+u*.6),'#b5edec')
    elif kind=='radiator' or kind=='solar':
        for k in range(1,5):line([(x+u*k/5,y+5),(x+u*k/5,y+u-5)],'#405f6e',2)
    elif kind=='thruster':
        rect((x+7,y+7,x+u-7,y+u-7),'#354c5b')
        rect((x+9,y+u-11,x+u-9,y+u-5),'#a9e0e2')
    elif kind=='weapon':
        ellipse((x+u*.22,y+u*.25,x+u*.78,y+u*.8),'#526a78','#bfcbd0')
        rect((x+u*.43,y-8,x+u*.57,y+u*.5),'#344957','#c2cbd0')
    elif kind=='reactor':
        ellipse((x+u*.2,y+u*.2,x+u*.8,y+u*.8),'#644f3d','#f0d391',2)
        line([(x+u*.5,y+u*.3),(x+u*.5,y+u*.7)],'#ffd693',2)
    else:
        rect((x+7,y+7,x+u-7,y+u-7),None,'#65706c',1)
        line([(x+9,y+9),(x+u-9,y+u-9)],'#5b696c',1)
def ship(cx,cy,u=43):
    # Coherent, illustrative layout: the core is enclosed by armour and functional wings.
    layout={}
    for row,cols in [(-3,range(-1,2)),(-2,range(-2,3)),(-1,range(-3,4)),(0,range(-3,4)),(1,range(-4,5)),(2,range(-3,4)),(3,[-3,-1,0,1,3])]:
        for col in cols:layout[col,row]='hull'
    layout[0,0]='core'
    for pos in [(-2,0),(2,0),(0,2)]:layout[pos]='reactor'
    for pos in [(-3,0),(3,0),(-4,1),(4,1)]:layout[pos]='radiator'
    for pos in [(-1,-3),(1,-3),(-3,-1),(3,-1)]:layout[pos]='weapon'
    for pos in [(-3,3),(-1,3),(1,3),(3,3)]:layout[pos]='thruster'
    for pos in [(-1,1),(1,1)]:layout[pos]='cargo'
    for (col,row),kind in sorted(layout.items(),key=lambda a:a[0][1]):
        module(cx+col*u,cy+row*u,u-2,kind)
    return layout
# 01: ship builder. The game world reaches every screen edge.
begin('#bfc9cd')
for y in range(74,625,26):line([(0,y),(1280,y)],'#b6c1c6')
for x in range(0,1280,26):line([(x,74),(x,625)],'#b6c1c6')
for x in range(12,1280,104):text(x,86,f'{(x-636)//26:+03}',12,'#8c9ba3')
ellipse((336,137,930,635),None,'#a7b6be',1)
ellipse((385,185,883,585),None,'#b0bec4',1)
line([(652,112),(652,616)],'#97aab4',1)
line([(260,377),(1008,377)],'#97aab4',1)
text(25,20,'舰船设计',26,'#2b404c',True);text(26,57,'种子舰 · 蓝图 01',14,'#516873')
text(26,101,'免费设计 / 试航验证',12,'#607781')
top_resources()
# Docking bay markings, kept secondary to the ship.
for xx in [336,918]:
    for yy in [160,568]:line([(xx,yy+28),(xx,yy),(xx+28,yy)],'#8b9da6',2)
text(465,603,'装配平面  /  船首 ↑',12,'#526b79')
ship(612,320,43)
# Weapon firing sectors
d.arc(pxy((473,58,837,422)),205,335,fill='#8d9da6',width=1*S)
line([(629,206),(521,77)],'#8d9da6');line([(673,206),(789,77)],'#8d9da6')
text(796,111,'炮口射界',12,'#5d737e')
# Actual geometric cues
arrow((631,482),(631,568),'#637d8b',2)
text(651,538,'主推力',14,'#435f6e')
ellipse((629,336,639,346),None,'#f5f1d9',2)
line([(634,331),(634,351)],'#344e60')
line([(624,341),(644,341)],'#344e60')
line([(636,342),(827,342),(857,312)],'#5a7480')
text(862,290,'重心',14,'#375566')
text(862,312,'与推力轴对齐',12,'#5f7884')
# Selected radiator
corner(612+4*43-4,320+43-4,47,47)
line([(803,383),(934,461),(1033,461)],'#b07739',1)
text(899,474,'散热翼',14,'#825c2e',True)
text(899,495,'冷却 +4.0',12,'#6d777b')
# Compact tools at right
panel(1054,81,202,481)
text(1070,96,'零件',17,'#e2e7e9',True)
text(1163,99,'8 / 8',12,'#859da9')
line([(1067,130),(1242,130)],'#43545e')
text(1071,140,'结构 / 机动 / 系统',12,'#a9bac3')
kinds=['hull','thruster','radiator','cargo','weapon','weapon','core','reactor']
labels=['装甲','推进','散热','货舱','激光','动能炮','护盾','反应堆']
for i,(k,l) in enumerate(zip(kinds,labels)):
    x=1072+(i%3)*58;y=177+(i//3)*78
    module(x+5,y,33,k)
    text(x+2,y+42,l,12,'#c3d0d6')
    if i==2:corner(x-2,y-7,51,65)
line([(1068,417),(1242,417)],'#43545e')
text(1070,432,'散热翼',16,'#f0c188',True)
text(1070,460,'2 × 1   /   质量 3',13,'#c5d2d8')
text(1070,487,'排出热量，维持持续火力。',12,'#a4b8c3')
text(1070,527,'Q / E 旋转    3 选择',12,'#8cabb8')
# selected part only inspector
panel(24,452,254,145)
text(40,467,'设计检查',14,'#d8e0e3',True)
text(40,498,'连接正常',14,'#9bc8b4');text(160,498,'无断开分支',12,'#92a5af')
text(40,527,'用电',12,'#94aab4');text(88,524,'18 / 24',18,'#ecf0ed')
text(40,561,'热负载',12,'#94aab4');rect((108,568,250,573),'#394b54');rect((108,568,189,573),'#c3a16e')
# bottom game command strip
rect((0,624,1280,720),'#1c282f')
line([(0,624),(1280,624)],'#52636b')
for i,t in enumerate(['行星','设计','航线']):
    x=24+i*78
    if i==1:line([(x,639),(x+58,639)],'#e1a255',3)
    text(x,651,t,17,'#edc18a' if i==1 else '#9cafb9',i==1)
for x,l,v in [(337,'质量','152'),(466,'推力','32'),(583,'护盾','44'),(700,'火力','28')]:
    text(x,642,l,12,'#8ca3af');text(x+42,639,v,22,'#e3e9e9')
text(337,682,'左键放置  ·  右键拆除  ·  滚轮缩放  ·  中键平移',12,'#8ca3af')
text(24,688,'REWORLD / 星构边疆',12,'#627f90')
panel(906,646,136,40,'#2c3d47');text(927,657,'保存蓝图',14,'#d8e0e4')
panel(1058,646,198,40,'#dba356');text(1087,656,'试航  [SPACE]',16,'#24333e',True)
text(1006,697,'构图提案 · 非实机',12,'#7f98a6')
save('01-ship-design-proposal.png')
# 02 Planet: low-poly, terrain-first layout; production is illustrative.
begin('#b5b6ac')
random.seed(10)
for i in range(55):
    x=random.randrange(-120,1250);y=random.randrange(70,680);r=random.randrange(30,150)
    c=random.choice(['#b0b2a8','#b9b9ad','#aeafa5','#bdbdb0'])
    poly([(x+math.cos(a)*r,y+math.sin(a)*r*.65) for a in [0,.8,2,3.3,4.1,5.2]],c)
for center in [(395,297),(820,580)]:
    cx,cy=center
    poly([(cx-100,cy-35),(cx-50,cy-82),(cx+81,cy-55),(cx+113,cy+32),(cx+18,cy+66),(cx-77,cy+45)],'#8a9b98')
    for i in range(23):
        x=cx+random.randrange(-75,70);y=cy+random.randrange(-40,40)
        poly([(x,y),(x+16,y-7),(x+28,y+3),(x+11,y+14)],random.choice(['#697d7e','#7d9290','#9bab9e']))
for i in range(18):
    x=random.randrange(290,1030);y=random.randrange(100,590)
    if 340<x<900 and 270<y<480:continue
    poly([(x,y),(x+20,y-14),(x+48,y-3),(x+53,y+16),(x+26,y+30),(x+8,y+20)],'#989b92','#868d87')
# selected construction grid only near build area
for x in range(354,898,48):line([(x,257),(x,498)],'#bfc7b9')
for y in range(257,499,48):line([(354,y),(898,y)],'#bfc7b9')
# industrial spine
for path in [[(432,340),(520,340),(520,397),(626,397)],[(626,397),(760,397)],[(626,397),(626,477),(831,477)]]:
    line(path,'#576763',12);line(path,'#a6b6a9',7)
    a,b=path[-2:];arrow((a[0]+(b[0]-a[0])*.4,a[1]+(b[1]-a[1])*.4),(a[0]+(b[0]-a[0])*.6,a[1]+(b[1]-a[1])*.6),'#decc8e',2)
module(385,308,70,'miner');module(580,354,86,'refinery');module(733,350,91,'cargo')
module(585,452,45,'core');module(806,445,70,'reactor')
for x in [454,511]:
    for y in [472,508]:module(x,y,32,'solar')
corner(576,350,94,94)
for xx in [599,627,646]:
    line([(xx,362),(xx-4,341),(xx+4,329)],'#e2c897',2)
text(590,322,'精炼炉 01',14,'#344c50',True)
text(378,270,'铁质矿脉',14,'#405c5b')
# Direct problem marker anchored to the problem
panel(690,245,172,49,'#504c3e');text(704,257,'!  输出满载',16,'#efd39b',True)
line([(719,294),(676,354)],'#7d704f',2)
text(25,22,'赤道据点',26,'#283c42',True);text(26,60,'地表 / 种子核心 01',14,'#53686a')
top_resources()
text(26,113,'恢复制造能力',16,'#334e53',True);text(26,142,'接通精炼设备的输出。',14,'#586c6b')
# Contextual inspection, not universal statistics
panel(24,388,264,215)
text(40,406,'精炼炉 01',19,'#e2e8e5',True)
text(40,439,'运行状态',12,'#8fa8b0');text(132,435,'输出满载',16,'#e1bd7e')
text(40,474,'铁质 → 合金',16,'#d8e1dd')
rect((40,506,269,511),'#41545a');rect((40,506,213,511),'#d1ad73')
text(40,527,'移走合金或连接空闲货仓。',14,'#bbc9cc')
text(40,569,'详 情  [I]       移 动  [M]',12,'#90a9b2')
# Bottom minimal hotbar, with world extending behind it
panel(340,622,636,76)
for i,k in enumerate(['solar','miner','refinery','radiator','cargo','reactor','hull']):
    x=357+i*78
    module(x+12,633,35,k);text(x+4,676,str(i+1),12,'#9cafb7')
    if i==2:corner(x+3,628,62,62)
panel(1040,639,216,52,'#344650');text(1064,654,'打开舰船设计  [TAB]',14,'#d4dfe2')
text(24,646,'行星   /   舰船   /   星区',14,'#3a545c',True)
text(24,685,'视觉提案 · 地表与物流为目标效果',12,'#4c6267')
save('02-planet-proposal.png')
# 03 Space: objects and combat space first.
begin('#101a26')
random.seed(22)
for i in range(450):
    x=random.randrange(1280);y=random.randrange(720);r=random.choice([.5,.6,.8,1.1])
    c=random.choice(['#304559','#3c4d60','#52616a','#273b4e'])
    ellipse((x-r,y-r,x+r,y+r),c)
for x,y,r in [(84,191,58),(213,525,43),(984,181,64),(1115,562,80),(1037,472,27)]:
    poly([(x+math.cos(a)*r,y+math.sin(a)*r*.7) for a in [0,.9,1.7,2.9,4.2,5.1]],'#293746','#41505c')
    line([(x-r*.5,y),(x,y-r*.4),(x+r*.5,y+r*.3)],'#465361',1)
# slight asteroid ring
d.arc(pxy((-85,112,1175,950)),182,340,fill='#253649',width=S)
ship(539,303,24)
for x in [539-3*24+11,539-24+11,539+24+11,539+3*24+11]:
    poly([(x-7,397),(x+7,397),(x,445)],'#548997')
    poly([(x-3,397),(x+3,397),(x,428)],'#c6edeb')
d.arc(pxy((419,207,705,498)),198,344,fill='#70c9da',width=2*S)
# enemy fractured geometry
for row,cols in [(-2,[-1,0,1]),(-1,[-2,-1,0,1,2]),(0,[-1,0,1]),(1,[-1,1])]:
    for col in cols:
        xx=920+col*23;yy=315+row*23
        poly([(xx,yy),(xx+20,yy),(xx+22,yy+20),(xx+3,yy+22)],'#9c6d70','#d1a1a0')
corner(852,250,140,125,'#cc7777')
text(855,222,'监管无人机',14,'#dbb9b8',True)
rect((855,395,989,398),'#563b49');rect((855,395,941,398),'#d28685')
for p in [(708,301),(747,296),(787,291)]:
    line([p,(p[0]+20,p[1]-3)],'#c4dfd8',2)
line([(933,326),(879,355)],'#df9285',2)
# cursor and selected target/weapon annotation
ellipse((777,273,800,296),None,'#d2dbd8');line([(789,267),(789,302)],'#a6b5bd');line([(771,285),(806,285)],'#a6b5bd')
text(25,22,'残骸区',24,'#d9e1e6',True)
text(26,60,'试航训练 / 01',14,'#829ba9')
text(26,105,'瘫痪无人机',16,'#d6dfe1');text(26,135,'0 / 3',22,'#e6be83',True)
text(1029,25,'P 暂停    ESC 返回',13,'#94a9b6')
# Minimal tactical radar
panel(1090,77,164,154,'#172633')
ellipse((1117,99,1229,211),None,'#3c5361')
line([(1173,96),(1173,214)],'#2c414f');line([(1115,155),(1231,155)],'#2c414f')
poly([(1173,151),(1168,162),(1178,162)],'#b9d7d7')
for x,y in [(1203,137),(1189,119),(1136,179)]:rect((x-2,y-2,x+2,y+2),'#c87d7c')
# edge instrumentation
panel(24,596,238,98,'#192a36')
text(40,609,'种子舰',16,'#dce5e6',True)
for i,(label,value,col) in enumerate([('护盾','38 / 44','#7bb6cf'),('舰体','完整','#a2bda8')]):
    y=639+i*23;text(40,y,label,12,'#8ca5b4');rect((88,y+7,179,y+11),'#334b5b');rect((88,y+7,166 if i==0 else 179,y+11),col);text(189,y,value,12,'#c6d4da')
panel(464,643,366,51,'#192a36')
text(480,652,'热量',12,'#a0b4c0')
rect((525,660,679,665),'#354756');rect((525,660,618,665),'#dfae66')
text(695,651,'36%',16,'#e1c18e')
text(480,676,'1 脉冲激光     2 动能炮',12,'#7f9baa')
text(1072,640,'速度  42 m/s',14,'#a6bbc8')
text(1072,670,'航向  082°',14,'#a6bbc8')
text(456,704,'视觉提案 · 模块化战斗舰体为目标效果',12,'#4e687a')
save('03-flight-proposal.png')
print('\n'.join(str(OUT/n) for n in ['01-ship-design-proposal.png','02-planet-proposal.png','03-flight-proposal.png']))

