namespace TestLib;

public interface IOrderRepo { void Save(string id); }

public class OrderService
{
    private readonly IOrderRepo _repo;
    public OrderService(IOrderRepo repo) { _repo = repo; }

    public void Place(string id) => _repo.Save(id);
}
