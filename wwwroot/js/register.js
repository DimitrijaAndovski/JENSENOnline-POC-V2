document.getElementById("registerForm").addEventListener("submit", async e => {
  e.preventDefault(); const msg=document.getElementById("message"); msg.textContent="";
  const pw=password.value;
  if (pw.length<8 || !/[A-Z]/.test(pw) || !/[a-z]/.test(pw) || !/[0-9]/.test(pw) || !/[^A-Za-z0-9]/.test(pw)) { msg.textContent="Lösenordet uppfyller inte kraven."; return; }
  const r=await fetch("/api/auth/register",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({email:email.value,password:pw})});
  msg.textContent=r.ok?"Konto skapat. Du kan logga in.":"Kontot kunde inte skapas.";
});
