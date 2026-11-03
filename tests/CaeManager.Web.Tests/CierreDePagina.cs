using Bunit;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cierra una página como lo haría el renderizador al retirarla, para los tests que afirman después sobre lo que su
/// <c>Dispose</c> libera (oyentes, tokens de consultas en vuelo, suscripciones).
/// </summary>
/// <remarks>
/// <c>DisposeComponentsAsync</c> de bUnit 2.11.3 vacía su lista de raíces justo después de encolar la baja en el
/// despachador (<c>BunitRenderer.DisposeComponents</c>): con el despachador ocupado (un repintado por aviso en curso, una
/// máquina cargada) la baja encuentra la lista ya vacía y no retira nada, sin error ni reintento posible, y la página
/// sigue viva y escuchando. Lanzada desde dentro del despachador (<c>cut.InvokeAsync</c>), la baja se serializa con
/// cualquier renderizado en vuelo y se ejecuta entera. Un test que siga fallando con este helper no es un problema del
/// instrumento: es una fuga real de la página.
/// </remarks>
internal static class CierreDePagina
{
    internal static Task CerrarPaginaAsync<T>(IRenderedComponent<T> cut, BunitContext contexto) where T : IComponent =>
        cut.InvokeAsync(contexto.DisposeComponentsAsync);
}