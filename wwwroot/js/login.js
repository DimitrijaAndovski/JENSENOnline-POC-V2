document.getElementById("loginForm").addEventListener("submit", async e => {
  e.preventDefault();
  const message = document.getElementById("message"); message.textContent = "";
  const response = await fetch("/api/auth/login", { method:"POST", credentials:"include", headers:{"Content-Type":"application/json"}, body:JSON.stringify({email:email.value,password:password.value}) });
  if (!response.ok) { message.textContent = response.status === 423 ? "Kontot är tillfälligt låst." : "Login misslyckades."; return; }
  location.href = "/";
});
