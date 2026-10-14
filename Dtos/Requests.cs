namespace JENSENOnline.POC.Dtos;

public record RegisterRequest(string Email, string Password);
public record LoginRequest(string Email, string Password);
public record ProductRequest(string Name, decimal Price, int Stock);
public record OrderLineRequest(int ProductId, int Quantity);
public record CreateOrderRequest(List<OrderLineRequest> Items);
