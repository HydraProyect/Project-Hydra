using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// D-11: <c>/salud</c> devuelve la revisión desplegada en una cabecera. El contrato
/// que los despliegues leen —código HTTP y cuerpo— no se toca: estas pruebas fijan
/// también que el siguiente eslabón sigue ejecutándose y que la cabecera no se
/// añade a ninguna otra ruta.
/// </summary>
public class RevisionDesplegadaEnSaludTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData(Sha, Sha)]
    [InlineData("  ABCDEF1  ", "abcdef1")]
    [InlineData(null, "desconocida")]
    [InlineData("", "desconocida")]
    [InlineData("abc", "desconocida")]
    [InlineData("0123456789abcdef0123456789abcdef012345678", "desconocida")]
    [InlineData("https://token@ejemplo.invalid/x", "desconocida")]
    [InlineData("sha-con-guiones", "desconocida")]
    public void Solo_se_acepta_un_sha_hexadecimal(string? entrada, string esperado) =>
        RevisionDesplegada.Leer(entrada).Should().Be(esperado);

    [Fact]
    public async Task Salud_lleva_la_cabecera_y_sigue_la_cadena()
    {
        var (contexto, respuesta) = Contexto("/salud");
        var siguienteEjecutado = false;

        await Pipeline(() => siguienteEjecutado = true)(contexto);
        await respuesta.DispararOnStartingAsync();

        siguienteEjecutado.Should().BeTrue("la cabecera no puede cortar la comprobación de salud");
        contexto.Response.Headers[RevisionDesplegada.Cabecera].ToString().Should().Be(Sha);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/saludable")]
    [InlineData("/api/v1/salud")]
    public async Task Otras_rutas_no_exponen_la_revision(string ruta)
    {
        var (contexto, respuesta) = Contexto(ruta);

        await Pipeline(() => { })(contexto);
        await respuesta.DispararOnStartingAsync();

        contexto.Response.Headers.ContainsKey(RevisionDesplegada.Cabecera).Should().BeFalse();
    }

    private static RequestDelegate Pipeline(Action alFinal)
    {
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        app.UseRevisionEnSalud(Sha);
        app.Run(_ =>
        {
            alFinal();
            return Task.CompletedTask;
        });
        return app.Build();
    }

    private static (DefaultHttpContext, RespuestaEspia) Contexto(string ruta)
    {
        var contexto = new DefaultHttpContext();
        contexto.Request.Path = ruta;
        // DefaultHttpContext ignora OnStarting: se registra aquí para poder dispararlo.
        var respuesta = new RespuestaEspia();
        contexto.Features.Set<IHttpResponseFeature>(respuesta);
        return (contexto, respuesta);
    }

    private sealed class RespuestaEspia : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Llamada, object Estado)> _alIniciar = [];

        public override void OnStarting(Func<object, Task> callback, object state) => _alIniciar.Add((callback, state));

        public async Task DispararOnStartingAsync()
        {
            foreach (var (llamada, estado) in _alIniciar)
                await llamada(estado);
        }
    }
}
