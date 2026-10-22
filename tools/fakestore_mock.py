"""Local stand-in for fakestoreapi.com, for offline development and for exercising failure modes.

Usage:  python3 tools/fakestore_mock.py [port]          (default 9100)
Modes:  GET /__mode/ok | /__mode/500 | /__mode/slow | /__mode/malformed
Data approximates the real catalogue; images are generated placeholder SVGs.
"""
import json, sys, time, colorsys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
P = [
 (1,"Fjallraven - Foldsack No. 1 Backpack, Fits 15 Laptops",109.95,"men's clothing",3.9,120),
 (2,"Mens Casual Premium Slim Fit T-Shirts ",22.3,"men's clothing",4.1,259),
 (3,"Mens Cotton Jacket",55.99,"men's clothing",4.7,500),
 (4,"Mens Casual Slim Fit",15.99,"men's clothing",2.1,430),
 (5,"John Hardy Women's Legends Naga Gold & Silver Dragon Station Chain Bracelet",695,"jewelery",4.6,400),
 (6,"Solid Gold Petite Micropave ",168,"jewelery",3.9,70),
 (7,"White Gold Plated Princess",9.99,"jewelery",3,400),
 (8,"Pierced Owl Rose Gold Plated Stainless Steel Double",10.99,"jewelery",1.9,100),
 (9,"WD 2TB Elements Portable External Hard Drive - USB 3.0 ",64,"electronics",3.3,203),
 (10,"SanDisk SSD PLUS 1TB Internal SSD - SATA III 6 Gb/s",109,"electronics",2.9,470),
 (11,"Silicon Power 256GB SSD 3D NAND A55 SLC Cache Performance Boost SATA III 2.5",109,"electronics",4.8,319),
 (12,"WD 4TB Gaming Drive Works with Playstation 4 Portable External Hard Drive",114,"electronics",4.8,400),
 (13,"Acer SB220Q bi 21.5 inches Full HD (1920 x 1080) IPS Ultra-Thin",599,"electronics",2.9,250),
 (14,"Samsung 49-Inch CHG90 144Hz Curved Gaming Monitor (LC49HG90DMNXZA) – Super Ultrawide Screen QLED ",999.99,"electronics",2.2,140),
 (15,"BIYLACLESEN Women's 3-in-1 Snowboard Jacket Winter Coats",56.99,"women's clothing",2.6,235),
 (16,"Lock and Love Women's Removable Hooded Faux Leather Moto Biker Jacket",29.95,"women's clothing",2.9,340),
 (17,"Rain Jacket Women Windbreaker Striped Climbing Raincoats",39.99,"women's clothing",3.8,679),
 (18,"MBJ Women's Solid Short Sleeve Boat Neck V ",9.85,"women's clothing",4.7,130),
 (19,"Opna Women's Short Sleeve Moisture",7.95,"women's clothing",4.5,146),
 (20,"DANVOUY Womens T Shirt Casual Cotton Short",12.99,"women's clothing",3.6,145),
]
PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 9100
BASE = f"http://localhost:{PORT}"
def product(p):
    i,t,pr,c,r,n = p
    return {"id":i,"title":t,"price":pr,"description":f"Sample description for {t.strip()}.","category":c,"image":f"{BASE}/img/{i}.svg","rating":{"rate":r,"count":n}}
MODE = {"value": "ok"}
def svg(i):
    h = (i*47 % 360)/360; r,g,b = [int(x*255) for x in colorsys.hls_to_rgb(h,.45,.35)]
    shape = ["<rect x='60' y='50' width='80' height='100' rx='10'/>","<circle cx='100' cy='100' r='52'/>","<path d='M50 70 L100 40 L150 70 L140 160 L60 160 Z'/>"][i%3]
    return f"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 200 200'><rect width='200' height='200' fill='white'/><g fill='rgb({r},{g},{b})'>{shape}</g></svg>"
class H(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def send(self, code, body, ctype="application/json"):
        b = body.encode(); self.send_response(code); self.send_header("Content-Type", ctype); self.send_header("Content-Length", str(len(b))); self.end_headers(); self.wfile.write(b)
    def do_GET(self):
        path = self.path.split("?")[0].rstrip("/")
        if path.startswith("/__mode/"): MODE["value"] = path.split("/")[-1]; return self.send(200, '{"mode":"%s"}' % MODE["value"])
        if path.startswith("/img/"): return self.send(200, svg(int(path.split("/")[-1].split(".")[0])), "image/svg+xml")
        m = MODE["value"]
        if m == "500": return self.send(500, '{"error":"boom"}')
        if m == "slow": time.sleep(15)
        if m == "malformed": return self.send(200, "<html>not json</html>", "text/html")
        if path == "/products": return self.send(200, json.dumps([product(p) for p in P]))
        if path == "/products/categories": return self.send(200, json.dumps(sorted({p[3] for p in P})))
        if path.startswith("/products/"):
            pid = path.split("/")[-1]
            match = [product(p) for p in P if str(p[0]) == pid]
            return self.send(200, json.dumps(match[0]) if match else "")  # real API: 200 + empty body
        self.send(404, "")
ThreadingHTTPServer(("127.0.0.1", PORT), H).serve_forever()
