namespace CaeManager.E2ETests;

/// <summary>
/// D-11: <c>/salud</c> de la aplicación real (pipeline completo, sin navegador ni
/// sesión) devuelve la cabecera de revisión <b>sin alterar</b> el contrato que leen los
/// despliegues: código 200 y cuerpo «Healthy». Detecta también que alguien quite el
/// cableado de <c>Program.cs</c>, que la prueba unitaria de la extensión no ve.
/// En este arnés la variable <c>TALVEG_REVISION</c> no está definida, así que el valor
/// esperado es «desconocida»: lo que se fija es que la cabecera exista y sea un valor válido.
/// </summary>
[Collection("AppCollection")]
public class SaludExponeLaRevisionTests(WebAppFixture fixture)
{
    [Fact]
    public async Task Salud_sigue_siendo_200_Healthy_y_lleva_la_cabecera_de_revision()
    {
        using var cliente = new HttpClient();
        var respuesta = await cliente.GetAsync($"{fixture.BaseUrl}/salud");

        Assert.Equal(System.Net.HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal("Healthy", await respuesta.Content.ReadAsStringAsync());
        Assert.True(respuesta.Headers.TryGetValues("X-Talveg-Revision", out var valores), "falta X-Talveg-Revision en /salud");
        var revision = Assert.Single(valores!);
        Assert.Matches("^([0-9a-f]{7,40}|desconocida)$", revision);
    }
}
