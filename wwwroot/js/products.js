let products=[];
async function loadProducts(search="") {
  const r=await fetch("/api/products?search="+encodeURIComponent(search)); products=await r.json();
  const root=document.getElementById("products"); root.replaceChildren();
  for(const p of products){
    const card=document.createElement("article"); card.className="card";
    const h=document.createElement("h3"); h.textContent=p.name;
    const price=document.createElement("p"); price.textContent=`${p.price} kr | Lager: ${p.stock}`;
    const btn=document.createElement("button"); btn.textContent="Köp 1"; btn.addEventListener("click",()=>buy(p.id));
    card.append(h,price,btn); root.appendChild(card);
  }
}
async function buy(productId){
  const r=await fetch("/api/orders",{method:"POST",credentials:"include",headers:{"Content-Type":"application/json"},body:JSON.stringify({items:[{productId,quantity:1}]})});
  document.getElementById("status").textContent=r.ok?"Order skapad.":r.status===401?"Logga in först.":"Ordern kunde inte skapas.";
}
document.getElementById("searchForm").addEventListener("submit",e=>{e.preventDefault();loadProducts(document.getElementById("search").value)});
loadProducts();
