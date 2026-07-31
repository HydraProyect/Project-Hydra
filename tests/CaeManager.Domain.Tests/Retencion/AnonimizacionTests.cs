using CaeManager.Domain.Documentos;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Retencion;

/// <summary>
/// Qué significa purgar en Hydra: anonimizar, no borrar
/// (RGPD-TRATAMIENTO-DATOS.md § 5, decidido por el propietario del producto
/// el 2026-07-31).
///
/// Lo que estos tests protegen es la propiedad que hace válida la
/// anonimización frente al RGPD: que <b>no quede ningún dato del que se pueda
/// reidentificar a la persona</b>. Conservar iniciales, un hash del DNI o el
/// año de nacimiento la convertiría en seudonimización, que sigue siendo dato
/// personal.
/// </summary>
public class AnonimizacionTests
{
    private static readonly DateTime Ahora = new(2031, 3, 10, 9, 0, 0, DateTimeKind.Utc);

    private static Trabajador CrearTrabajador() => Trabajador.DeEmpresa(
        Guid.NewGuid(), "Manuel", "Moreno Domínguez", "12345678Z",
        new DateOnly(1985, 4, 12), "manuel.moreno@ejemplo.test", "Alergia declarada al látex", "Manu");

    [Fact]
    public void Anonimizar_un_trabajador_no_deja_rastro_de_la_persona()
    {
        var trabajador = CrearTrabajador();

        trabajador.Anonimizar(Ahora);

        trabajador.Apellidos.Should().BeEmpty();
        trabajador.Dni.Should().BeEmpty();
        trabajador.Email.Should().BeNull();
        trabajador.FechaNacimiento.Should().BeNull();
        trabajador.Alias.Should().BeNull();
        // Las observaciones pueden contener datos de salud: es el campo que
        // más importa que desaparezca.
        trabajador.Observaciones.Should().BeNull();

        trabajador.EstaAnonimizado.Should().BeTrue();
        trabajador.AnonimizadoEnUtc.Should().Be(Ahora);
    }

    [Fact]
    public void El_nombre_que_queda_no_permite_reidentificar()
    {
        var trabajador = CrearTrabajador();

        trabajador.Anonimizar(Ahora);

        trabajador.Nombre.Should().NotContain("Manuel");
        trabajador.Nombre.Should().NotContain("Moreno");
        trabajador.Nombre.Should().NotContain("Manu");
        trabajador.NombreCompleto.Should().NotContain("12345678");
    }

    [Fact]
    public void Se_conserva_la_fila_y_su_vinculo_con_el_empleador()
    {
        // Borrar la fila rompería el histórico de coordinación, que sin datos
        // identificativos ya no es dato personal y sigue siendo necesario.
        var trabajador = CrearTrabajador();
        var empresaId = trabajador.EmpresaId;
        var id = trabajador.Id;

        trabajador.Anonimizar(Ahora);

        trabajador.Id.Should().Be(id);
        trabajador.EmpresaId.Should().Be(empresaId);
    }

    [Fact]
    public void Anonimizar_dos_veces_no_mueve_la_fecha()
    {
        var trabajador = CrearTrabajador();
        trabajador.Anonimizar(Ahora);

        trabajador.Anonimizar(Ahora.AddYears(1));

        trabajador.AnonimizadoEnUtc.Should().Be(Ahora, "la supresión ocurrió una vez y esa es la fecha que hay que poder demostrar");
    }

    [Fact]
    public void Un_trabajador_anonimizado_no_admite_que_le_reintroduzcan_datos()
    {
        // Sin esto, editarlo dejaría la supresión sin efecto en silencio.
        var trabajador = CrearTrabajador();
        trabajador.Anonimizar(Ahora);

        var editar = () => trabajador.Actualizar(
            "Manuel", "Moreno Domínguez", new DateOnly(1985, 4, 12), "manuel@ejemplo.test", null, null);

        editar.Should().Throw<InvalidOperationException>().WithMessage("*no pueden volver a introducirse*");
    }

    [Fact]
    public void Anonimizar_un_documento_devuelve_el_archivo_que_hay_que_borrar()
    {
        // El dato personal está DENTRO del PDF, así que suprimirlo de verdad
        // exige borrar el fichero. Si esta operación no devolviera la ruta, el
        // archivo quedaría huérfano y la supresión sería aparente.
        var documento = Documento.DeTrabajador(
            Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 15), new DateOnly(2027, 1, 15),
            "tenant/documentos/reconocimiento-medico.pdf", "Apto con restricciones");

        var archivo = documento.Anonimizar(Ahora);

        archivo.Should().Be("tenant/documentos/reconocimiento-medico.pdf");
        documento.ArchivoUrl.Should().BeNull();
        documento.Comentarios.Should().BeNull("un comentario puede describir el contenido del documento");
        documento.EstaAnonimizado.Should().BeTrue();
    }

    [Fact]
    public void Un_documento_anonimizado_conserva_lo_que_sostiene_el_historico()
    {
        var emision = new DateOnly(2026, 1, 15);
        var vencimiento = new DateOnly(2027, 1, 15);
        var documento = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), emision, vencimiento, "ruta.pdf");

        documento.Anonimizar(Ahora);

        documento.FechaEmision.Should().Be(emision);
        documento.FechaVencimiento.Should().Be(vencimiento);
        documento.TipoDocumentoId.Should().NotBeEmpty();
    }

    [Fact]
    public void Anonimizar_un_documento_dos_veces_no_pide_borrar_nada_otra_vez()
    {
        var documento = Documento.DeTrabajador(
            Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 15), null, "ruta.pdf");
        documento.Anonimizar(Ahora);

        documento.Anonimizar(Ahora.AddDays(1)).Should().BeNull();
        documento.AnonimizadoEnUtc.Should().Be(Ahora);
    }
}
