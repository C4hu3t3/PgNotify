namespace HttpCaching.WebApi;

/// <summary>
/// A plain entity with no notification attribute at all: notifications are configured fluently in
/// <see cref="SampleDbContext.OnModelCreating"/>, which is where this sample asks for the
/// <b>extended</b> payload — the only shape carrying the <c>timestamp</c> field its ETags are built
/// from. The default, either style, is the minimal payload, which has no timestamp.
/// </summary>
public class Article
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Tag { get; set; } = "general";
}
