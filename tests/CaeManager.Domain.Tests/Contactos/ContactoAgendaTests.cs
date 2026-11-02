using CaeManager.Domain.Contactos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Contactos;

public class ContactoAgendaTests
{
    private static ContactoAgenda CrearContacto() =>
        ContactoAgenda.DeEmpresa(Guid.NewGuid(), "Juan Pérez", "juan@example.com");

    [Fact]
    public void FijarNotas_guarda_texto_autor_y_fecha()
    {
        var contacto = CrearContacto();
        var autor = Guid.NewGuid();
        var ahora = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);

        contacto.FijarNotas("  Llamar antes de las 11  ", autor, ahora);

        contacto.Notas.Should().Be("Llamar antes de las 11");
        contacto.NotasAutorUsuarioId.Should().Be(autor);
        contacto.NotasActualizadasEnUtc.Should().Be(ahora);
    }

    [Fact]
    public void FijarNotas_con_el_mismo_texto_no_cambia_la_autoria()
    {
        var contacto = CrearContacto();
        var autor = Guid.NewGuid();
        var ahora = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);
        contacto.FijarNotas("Llamar antes de las 11", autor, ahora);

        contacto.FijarNotas("Llamar antes de las 11", Guid.NewGuid(), ahora.AddDays(1));

        contacto.NotasAutorUsuarioId.Should().Be(autor);
        contacto.NotasActualizadasEnUtc.Should().Be(ahora);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void FijarNotas_vacia_retira_tambien_la_autoria(string? vacia)
    {
        var contacto = CrearContacto();
        contacto.FijarNotas("Llamar antes de las 11", Guid.NewGuid(), DateTime.UtcNow);

        contacto.FijarNotas(vacia, Guid.NewGuid(), DateTime.UtcNow);

        contacto.Notas.Should().BeNull();
        contacto.NotasAutorUsuarioId.Should().BeNull();
        contacto.NotasActualizadasEnUtc.Should().BeNull();
    }

    [Fact]
    public void Nace_sin_roles()
    {
        var contacto = CrearContacto();

        contacto.Roles.Should().BeEmpty();
    }

    [Fact]
    public void EstablecerRoles_asigna_varios_roles_a_la_vez()
    {
        var contacto = CrearContacto();

        contacto.EstablecerRoles([RolContacto.ResponsablePrl, RolContacto.ContactoCae]);

        contacto.Roles.Should().HaveCount(2);
        contacto.Roles.Select(r => r.Rol).Should().BeEquivalentTo([RolContacto.ResponsablePrl, RolContacto.ContactoCae]);
    }

    [Fact]
    public void EstablecerRoles_ignora_duplicados()
    {
        var contacto = CrearContacto();

        contacto.EstablecerRoles([RolContacto.ResponsablePrl, RolContacto.ResponsablePrl]);

        contacto.Roles.Should().ContainSingle();
    }

    [Fact]
    public void EstablecerRoles_reemplaza_el_conjunto_completo_en_vez_de_acumular()
    {
        var contacto = CrearContacto();
        contacto.EstablecerRoles([RolContacto.ResponsablePrl, RolContacto.RepresentanteLegal]);

        contacto.EstablecerRoles([RolContacto.ContactoCae]);

        contacto.Roles.Should().ContainSingle(r => r.Rol == RolContacto.ContactoCae);
    }

    [Fact]
    public void EstablecerRoles_con_lista_vacia_deja_el_contacto_sin_roles()
    {
        var contacto = CrearContacto();
        contacto.EstablecerRoles([RolContacto.ResponsablePrl]);

        contacto.EstablecerRoles([]);

        contacto.Roles.Should().BeEmpty();
    }
}
