using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Las escrituras en la query de la página actual pasan por <c>NavigationManagerExtensions.ActualizarFiltroEnUrl</c>
/// (o <c>ActualizarFiltrosEnUrl</c>), que compone varias escrituras seguidas sobre el último destino pedido (#1737).
/// Quien compone a mano sobre <c>NavigationManager.Uri</c> lee la URL todavía sin la escritura anterior y la pisa.
/// <para>
/// El detector cuenta las llamadas que componen sobre la URL actual: <c>GetUriWithQueryParameter</c> (siempre parte de
/// <c>Uri</c>), <c>GetUriWithQueryParameters</c> con solo el diccionario, y <c>GetUriWithQueryParameters</c> con
/// <c>….Uri</c> como base. Un enlace a OTRA página con su propia query (<c>NavigateTo($"/visitas?centroId={id}")</c>,
/// <c>GetUriWithQueryParameters("/cuenta/verificar-2fa", …)</c>) no compone sobre la URL actual y no cuenta.
/// </para>
/// </summary>
public class QueriesSobreLaUriActualSinElAyudanteTests
{
    private const string Lista = "Query-sobre-Uri-actual-sin-ayudante";
    private const string Simbolo = "GetUriWithQueryParameter-sobre-Uri";
    private const string Ayudante = "src/CaeManager.Web/Components/NavigationManagerExtensions.cs";

    [Fact]
    public void Las_composiciones_manuales_sobre_la_URL_actual_solo_pueden_disminuir()
    {
        var medidas = new Dictionary<Ubicacion, int>();
        foreach (var archivo in FuentesDeSrc.Archivos())
        {
            var relativa = FuentesDeSrc.Relativa(archivo);
            var razor = archivo.EndsWith(".razor", StringComparison.Ordinal);
            if (!relativa.StartsWith("src/CaeManager.Web/", StringComparison.Ordinal)
                || relativa == Ayudante
                || !(razor || archivo.EndsWith(".cs", StringComparison.Ordinal)))
                continue;

            var cantidad = Contar(LimpiadorDeComentarios.Quitar(File.ReadAllText(archivo), razor));
            if (cantidad > 0)
                medidas.Add(new(relativa, Simbolo), cantidad);
        }

        ListaCongelada.Verificar(Lista, medidas,
            "Para escribir parámetros en la URL de la página actual usa ActualizarFiltroEnUrl o ActualizarFiltrosEnUrl " +
            "(estadoHistorial: null si la escritura debe pasar por el aviso de cambios sin guardar); al resolver un uso, " +
            "baja la lista.")
            .Should().BeNull();
    }

    // Sintáctico: no sigue variables. Una base distinta de «….Uri» se toma por otra página.
    internal static int Contar(string fuente)
    {
        var cantidad = 0;
        foreach (Match llamada in Regex.Matches(fuente, @"\b(GetUriWithQueryParameters?)\s*\("))
        {
            if (SyntaxFactory.ParseExpression(fuente[llamada.Index..]) is not InvocationExpressionSyntax invocacion)
                continue;

            var argumentos = invocacion.ArgumentList.Arguments;
            var componeSobreLaActual = llamada.Groups[1].Value == "GetUriWithQueryParameter"
                || argumentos.Count == 1
                || (argumentos.Count == 2 && EsLaUriActual(argumentos[0].Expression));
            if (componeSobreLaActual)
                cantidad++;
        }
        return cantidad;
    }

    private static bool EsLaUriActual(ExpressionSyntax expresion) =>
        expresion is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Uri" }
            or IdentifierNameSyntax { Identifier.ValueText: "Uri" };

    [Theory]
    [InlineData("Navigation.NavigateTo(Navigation.GetUriWithQueryParameter(\"pestana\", p), replace: true);", 1)]
    [InlineData("var destino = Navegacion.GetUriWithQueryParameters(new Dictionary<string, object?>\n{\n    [\"q\"] = q\n});", 1)]
    [InlineData("var destino = Navigation.GetUriWithQueryParameters(Navigation.Uri, parametros);", 1)]
    [InlineData("<a href=\"@Navigation.GetUriWithQueryParameter(\"orden\", \"fecha\")\">Fecha</a>", 1)]
    [InlineData("Navigation.GetUriWithQueryParameter(\n    \"q\",\n    valor)", 1)]
    [InlineData("return Navigation.GetUriWithQueryParameters(\"/cuenta/verificar-2fa\", parametros);", 0)]
    [InlineData("Navigation.GetUriWithQueryParameters(RutaErrorDelSistema, parametros);", 0)]
    [InlineData("Navigation.NavigateTo($\"/visitas?centroId={id}\");", 0)]
    [InlineData("Navigation.ActualizarFiltroEnUrl(\"pestana\", p, estadoHistorial: null);", 0)]
    public void El_detector_distingue_la_URL_actual_de_otra_pagina(string fuente, int esperado) =>
        Contar(fuente).Should().Be(esperado);
}
