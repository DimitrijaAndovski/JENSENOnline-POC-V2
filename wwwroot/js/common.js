const logoutButton = document.getElementById("logout");
if (logoutButton) logoutButton.addEventListener("click", async () => {
  await fetch("/api/auth/logout", { method: "POST", credentials: "include" });
  location.href = "/login.html";
});
