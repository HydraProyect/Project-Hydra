using CaeManager.Application.AltaAutonoma;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.AltaAutonoma;

public class ServicioRegistroAltaTests
{
    private static readonly Uri Base = new("https://app.ejemplo.test/");

    [Fact]
    public async Task Un_correo_nuevo_abre_la_Solicitud_de_alta_y_envia_un_enlace_cuyo_token_solo_se_guarda_como_hash()
    {
        var (servicio, acceso, remitente) = Crear(existeCuenta: false);

        await servicio.IniciarAsync(Datos("Persona@Empresa.es"), Base, CancellationToken.None);

        var solicitud = acceso.Iniciadas.Should().ContainSingle().Subject;
        solicitud.Estado.Should().Be(EstadoSolicitudAlta.Iniciada);
        solicitud.CorreoNormalizado.Should().Be("PERSONA@EMPRESA.ES");
        solicitud.HashContrasena.Should().Be("hash-de-identity");

        var (destinatario, asunto, cuerpo) = remitente.Enviados.Should().ContainSingle().Subject;
        destinatario.Should().Be("Persona@Empresa.es");
        asunto.Should().Be(ServicioRegistroAlta.AsuntoVerificacion);
        var token = TokenDelEnlace(cuerpo);
        solicitud.HashTokenVerificacion.Should().Be(SolicitudAlta.CalcularHashToken(token));
        cuerpo.Should().NotContain(solicitud.HashTokenVerificacion!, "el correo lleva el token, la base solo su hash");
    }

    [Fact]
    public async Task Un_correo_con_cuenta_no_abre_nada_y_recibe_otro_aviso()
    {
        var (servicio, acceso, remitente) = Crear(existeCuenta: true);

        await servicio.IniciarAsync(Datos("ya@empresa.es"), Base, CancellationToken.None);

        acceso.Iniciadas.Should().BeEmpty("RQ-28: no se crea una Solicitud de alta para un correo con cuenta");
        var (destinatario, asunto, cuerpo) = remitente.Enviados.Should().ContainSingle().Subject;
        destinatario.Should().Be("ya@empresa.es");
        asunto.Should().Be(ServicioRegistroAlta.AsuntoCuentaExistente);
        cuerpo.Should().Contain("cuenta/olvide-contrasena").And.NotContain(ServicioRegistroAlta.RutaVerificacion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("corto")]
    public async Task Un_token_ausente_o_corto_no_llega_a_la_base(string? token)
    {
        var (servicio, acceso, _) = Crear(existeCuenta: false);

        (await servicio.VerificarCorreoAsync(token, CancellationToken.None)).Should().Be(ResultadoVerificacionCorreoAlta.NoValido);
        acceso.Verificaciones.Should().Be(0);
    }

    [Fact]
    public void Los_tokens_son_de_256_bits_y_seguros_en_una_URL()
    {
        var tokens = Enumerable.Range(0, 50).Select(_ => ServicioRegistroAlta.NuevoToken()).ToList();

        tokens.Should().OnlyHaveUniqueItems();
        tokens.Should().AllSatisfy(t =>
        {
            t.Length.Should().Be(43);
            t.Should().MatchRegex("^[A-Za-z0-9_-]+$");
        });
    }

    private static string TokenDelEnlace(string cuerpo)
    {
        var inicio = cuerpo.IndexOf("token=", StringComparison.Ordinal) + "token=".Length;
        return cuerpo[inicio..cuerpo.IndexOf('"', inicio)];
    }

    private static DatosRegistroAlta Datos(string correo) => new(correo, "hash-de-identity", "203.0.113.9", "Navegador");

    private static (ServicioRegistroAlta, AccesoFalso, RemitenteFalso) Crear(bool existeCuenta)
    {
        var acceso = new AccesoFalso();
        var remitente = new RemitenteFalso();
        var servicio = new ServicioRegistroAlta(acceso, new CuentasFalsas(existeCuenta), remitente, TimeProvider.System,
            NullLogger<ServicioRegistroAlta>.Instance);
        return (servicio, acceso, remitente);
    }

    private sealed class AccesoFalso : IAccesoSolicitudesAlta
    {
        public List<SolicitudAlta> Iniciadas { get; } = [];
        public int Verificaciones { get; private set; }

        public Task IniciarAsync(SolicitudAlta solicitud, CancellationToken cancellationToken)
        {
            Iniciadas.Add(solicitud);
            return Task.CompletedTask;
        }

        public Task<ResultadoVerificacionCorreoAlta> VerificarCorreoAsync(string token, CancellationToken cancellationToken)
        {
            Verificaciones++;
            return Task.FromResult(ResultadoVerificacionCorreoAlta.Verificado);
        }
    }

    private sealed class CuentasFalsas(bool existe) : IExistenciaCuentaPorCorreo
    {
        public Task<bool> ExisteAsync(string correoNormalizado, CancellationToken cancellationToken) => Task.FromResult(existe);
    }

    private sealed class RemitenteFalso : IRemitenteCorreoAlta
    {
        public List<(string Destinatario, string Asunto, string Cuerpo)> Enviados { get; } = [];

        public Task<Result> EnviarAsync(string destinatario, string asunto, string cuerpoHtml, CancellationToken cancellationToken)
        {
            Enviados.Add((destinatario, asunto, cuerpoHtml));
            return Task.FromResult(Result.Exito());
        }
    }
}
