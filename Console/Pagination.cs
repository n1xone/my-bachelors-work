namespace Bakalarka.ConsoleApp;

internal static class Pagination
{
    public static async Task<List<T>> ReadAllAsync<T>(Func<int, int, Task<IReadOnlyList<T>>> readPage)
    {
        const int pageSize = 100;
        var result = new List<T>();

        for (var offset = 0; ; offset += pageSize)
        {
            var page = await readPage(offset, pageSize);
            result.AddRange(page);

            if (page.Count < pageSize)
                return result;
        }
    }
}
