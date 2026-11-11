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

        (await servicio.VerificarCorreoAsync(token, "contraseña", CancellationToken.None)).Resultado
            .Should().Be(ResultadoVerificacionCorreoAlta.NoValido);
        acceso.Consultas.Should().Be(0);
        acceso.Verificaciones.Should().Be(0);
    }

    [Fact]
    public async Task Con_token_y_contrasena_que_casan_verifica_y_entrega_un_credencial_de_continuacion_de_256_bits()
    {
        var (servicio, acceso, _) = Crear(existeCuenta: false);

        var verificacion = await servicio.VerificarCorreoAsync(ServicioRegistroAlta.NuevoToken(), "la-buena", CancellationToken.None);

        verificacion.Resultado.Should().Be(ResultadoVerificacionCorreoAlta.Verificado);
        verificacion.CredencialContinuacion.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        acceso.Verificaciones.Should().Be(1);
        acceso.HashComprobado.Should().Be("hash-guardado", "la segunda función recibe el mismo hash que se comparó");
        acceso.CredencialRecibido.Should().Be(verificacion.CredencialContinuacion);
    }

    [Theory]
    [InlineData("la-mala")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Una_contrasena_que_no_casa_responde_como_un_enlace_no_valido_y_no_gasta_el_enlace(string? contrasena)
    {
        var (servicio, acceso, _) = Crear(existeCuenta: false);

        var verificacion = await servicio.VerificarCorreoAsync(ServicioRegistroAlta.NuevoToken(), contrasena, CancellationToken.None);

        verificacion.Should().Be(new VerificacionCorreoAlta(ResultadoVerificacionCorreoAlta.NoValido, null));
        acceso.Consultas.Should().Be(1);
        acceso.Verificaciones.Should().Be(0, "sin la contraseña el enlace no se gasta");
    }

    [Theory]
    [InlineData(ResultadoVerificacionCorreoAlta.NoValido)]
    [InlineData(ResultadoVerificacionCorreoAlta.EnlaceCaducado)]
    [InlineData(ResultadoVerificacionCorreoAlta.SolicitudCaducada)]
    public async Task Un_enlace_rechazado_no_compara_la_contrasena_ni_verifica(ResultadoVerificacionCorreoAlta rechazo)
    {
        var (servicio, acceso, _) = Crear(existeCuenta: false);
        acceso.Enlace = new EnlaceVerificacionAlta(rechazo, null);

        var verificacion = await servicio.VerificarCorreoAsync(ServicioRegistroAlta.NuevoToken(), "la-buena", CancellationToken.None);

        verificacion.Should().Be(new VerificacionCorreoAlta(rechazo, null));
        acceso.Verificaciones.Should().Be(0);
    }

    [Fact]
    public async Task Si_la_base_no_verifica_bajo_candado_no_se_entrega_credencial()
    {
        var (servicio, acceso, _) = Crear(existeCuenta: false);
        acceso.ResultadoVerificacion = ResultadoVerificacionCorreoAlta.NoValido;

        var verificacion = await servicio.VerificarCorreoAsync(ServicioRegistroAlta.NuevoToken(), "la-buena", CancellationToken.None);

        verificacion.Should().Be(new VerificacionCorreoAlta(ResultadoVerificacionCorreoAlta.NoValido, null));
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
        var servicio = new ServicioRegistroAlta(acceso, new CuentasFalsas(existeCuenta), remitente, new ContrasenasFalsas(),
            TimeProvider.System, NullLogger<ServicioRegistroAlta>.Instance);
        return (servicio, acceso, remitente);
    }

    internal sealed class AccesoFalso : IAccesoSolicitudesAlta
    {
        public List<SolicitudAlta> Iniciadas { get; } = [];
        public int Consultas { get; private set; }
        public int Verificaciones { get; private set; }
        public string? HashComprobado { get; private set; }
        public string? CredencialRecibido { get; private set; }
        public EnlaceVerificacionAlta Enlace { get; set; } = new(null, "hash-guardado");
        public ResultadoVerificacionCorreoAlta ResultadoVerificacion { get; set; } = ResultadoVerificacionCorreoAlta.Verificado;
        public TransicionDatosOrganizacionAlta Transicion { get; set; } = new(ResultadoDatosOrganizacionAlta.DatosCompletos, Guid.NewGuid());
        public int Completados { get; private set; }

        public Task IniciarAsync(SolicitudAlta solicitud, CancellationToken cancellationToken)
        {
            Iniciadas.Add(solicitud);
            return Task.CompletedTask;
        }

        public Task<EnlaceVerificacionAlta> ConsultarEnlaceAsync(string token, CancellationToken cancellationToken)
        {
            Consultas++;
            return Task.FromResult(Enlace);
        }

        public Task<ResultadoVerificacionCorreoAlta> VerificarCorreoAsync(
            string token, string hashContrasenaComprobado, string credencialContinuacion, CancellationToken cancellationToken)
        {
            Verificaciones++;
            HashComprobado = hashContrasenaComprobado;
            CredencialRecibido = credencialContinuacion;
            return Task.FromResult(ResultadoVerificacion);
        }

        public Task<TransicionDatosOrganizacionAlta> CompletarDatosOrganizacionAsync(
            string credencialContinuacion, DatosOrganizacionAlta datos, CancellationToken cancellationToken)
        {
            Completados++;
            return Task.FromResult(Transicion);
        }

        public EstadoContinuacionAlta Continuacion { get; set; } = EstadoContinuacionAlta.CorreoVerificado;
        public int ConsultasContinuacion { get; private set; }

        public Task<EstadoContinuacionAlta> ConsultarContinuacionAsync(string credencialContinuacion, CancellationToken cancellationToken)
        {
            ConsultasContinuacion++;
            return Task.FromResult(Continuacion);
        }
    }

    /// <summary>Solo «la-buena» casa con «hash-guardado».</summary>
    private sealed class ContrasenasFalsas : IPreparacionContrasenaAlta
    {
        public Task<string?> ValidarYHashearAsync(string correo, string contrasena) => Task.FromResult<string?>("hash-de-identity");

        public bool Coincide(string hashContrasena, string contrasena) => hashContrasena == "hash-guardado" && contrasena == "la-buena";
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
