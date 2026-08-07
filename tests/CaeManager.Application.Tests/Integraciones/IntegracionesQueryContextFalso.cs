using System.Linq.Expressions;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore.Query;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>
/// Fake en memoria de <see cref="IIntegracionesQueryContext"/>. Un
/// <c>List.AsQueryable()</c> normal no soporta los operadores asíncronos de
/// EF Core (<c>FirstOrDefaultAsync</c>, <c>AnyAsync</c>…) porque su proveedor
/// LINQ-to-Objects no implementa <see cref="IAsyncEnumerable{T}"/> — de ahí
/// el envoltorio <see cref="TestAsyncQueryable{T}"/>, el patrón estándar
/// recomendado por Microsoft para probar handlers que usan IQueryable de EF
/// Core sin una base de datos real.
/// </summary>
public class IntegracionesQueryContextFalso : IIntegracionesQueryContext
{
    public List<ConexionIntegracion> ConexionesLista { get; } = [];
    public List<LineaWhatsApp> LineasLista { get; } = [];
    public List<MiembroPoolLinea> MiembrosLista { get; } = [];

    public IQueryable<ConexionIntegracion> ConexionesIntegracion => new TestAsyncQueryable<ConexionIntegracion>(ConexionesLista.AsQueryable());
    public IQueryable<LineaWhatsApp> LineasWhatsApp => new TestAsyncQueryable<LineaWhatsApp>(LineasLista.AsQueryable());
    public IQueryable<MiembroPoolLinea> MiembrosPoolLinea => new TestAsyncQueryable<MiembroPoolLinea>(MiembrosLista.AsQueryable());
}

internal class TestAsyncQueryable<T>(IQueryable<T> interna) : IOrderedQueryable<T>, IAsyncEnumerable<T>
{
    public Type ElementType => interna.ElementType;
    public Expression Expression => interna.Expression;
    public IQueryProvider Provider { get; } = new TestAsyncQueryProvider<T>(interna.Provider);

    public IEnumerator<T> GetEnumerator() => interna.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => interna.GetEnumerator();

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new TestAsyncEnumerator<T>(interna.GetEnumerator());
}

internal class TestAsyncQueryProvider<T>(IQueryProvider interno) : IAsyncQueryProvider
{
    public IQueryable CreateQuery(Expression expression) => new TestAsyncQueryable<T>(interno.CreateQuery<T>(expression));

    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new TestAsyncQueryable<TElement>(interno.CreateQuery<TElement>(expression));

    public object? Execute(Expression expression) => interno.Execute(expression);

    public TResult Execute<TResult>(Expression expression) => interno.Execute<TResult>(expression);

    // Desenvuelve TResult de Task<TResult> — es lo que Async LINQ (FirstOrDefaultAsync, AnyAsync…) espera del proveedor.
    public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
    {
        var tipoResultado = typeof(TResult).GetGenericArguments().FirstOrDefault() ?? typeof(TResult);
        var resultado = interno.Execute(expression);
        var metodoFromResult = typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(tipoResultado);
        return (TResult)metodoFromResult.Invoke(null, [resultado])!;
    }
}

internal class TestAsyncEnumerator<T>(IEnumerator<T> interno) : IAsyncEnumerator<T>
{
    public T Current => interno.Current;

    public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(interno.MoveNext());

    public ValueTask DisposeAsync()
    {
        interno.Dispose();
        return ValueTask.CompletedTask;
    }
}
