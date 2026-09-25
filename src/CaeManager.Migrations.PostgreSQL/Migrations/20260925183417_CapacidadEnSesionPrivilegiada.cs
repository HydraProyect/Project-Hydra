using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// La Sesión Privilegiada guarda la capacidad de su concesión (hallazgo de
    /// Codex en ADR-011 § 8.7, incremento 2). El Administrador del Tenant
    /// objetivo ve sus sesiones, pero no la concesión: sin esta columna una
    /// sesión de <c>Aprovisionamiento</c>, que escribe, se leía igual que una de
    /// <c>SoporteLectura</c>.
    ///
    /// <para>
    /// <b>Relleno.</b> Las filas existentes toman la capacidad de su concesión.
    /// Ambas tablas tienen FORCE ROW LEVEL SECURITY, y como propietario sin
    /// <c>app.usuario_id</c> el UPDATE no vería ninguna fila y la clave foránea
    /// fallaría sobre las que quedaran con la cadena vacía. Por eso se retira el
    /// FORCE solo durante el relleno, dentro de la transacción de la migración
    /// (la DDL bloquea las tablas hasta el COMMIT, nadie más las lee así), y se
    /// restablece antes de terminar. Las políticas no cambian.
    /// </para>
    ///
    /// <para>
    /// <b>No puede discrepar.</b> Clave foránea compuesta
    /// (<c>ConcesionPrivilegioId</c>, <c>Capacidad</c>) contra
    /// (<c>Id</c>, <c>Capacidad</c>) de la concesión: una sesión no puede decir
    /// «SoporteLectura» si su concesión es de otra capacidad. Se declara en SQL,
    /// no en el modelo: EF solo conoce la navegación simple por
    /// <c>ConcesionPrivilegioId</c> (ver <c>SesionPrivilegiadaConfiguration</c>).
    /// </para>
    /// </summary>
    public partial class CapacidadEnSesionPrivilegiada : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Capacidad",
                table: "SesionesPrivilegiadas",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(@"
ALTER TABLE ""SesionesPrivilegiadas"" NO FORCE ROW LEVEL SECURITY;
ALTER TABLE ""ConcesionesPrivilegio"" NO FORCE ROW LEVEL SECURITY;

UPDATE ""SesionesPrivilegiadas"" s
   SET ""Capacidad"" = c.""Capacidad""
  FROM ""ConcesionesPrivilegio"" c
 WHERE c.""Id"" = s.""ConcesionPrivilegioId"";

ALTER TABLE ""SesionesPrivilegiadas"" FORCE ROW LEVEL SECURITY;
ALTER TABLE ""ConcesionesPrivilegio"" FORCE ROW LEVEL SECURITY;

ALTER TABLE ""SesionesPrivilegiadas"" ALTER COLUMN ""Capacidad"" DROP DEFAULT;

ALTER TABLE ""ConcesionesPrivilegio""
    ADD CONSTRAINT ""AK_ConcesionesPrivilegio_Id_Capacidad"" UNIQUE (""Id"", ""Capacidad"");
ALTER TABLE ""SesionesPrivilegiadas""
    ADD CONSTRAINT ""FK_SesionesPrivilegiadas_Concesion_Capacidad""
    FOREIGN KEY (""ConcesionPrivilegioId"", ""Capacidad"")
    REFERENCES ""ConcesionesPrivilegio"" (""Id"", ""Capacidad"") ON DELETE RESTRICT;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
ALTER TABLE ""SesionesPrivilegiadas"" DROP CONSTRAINT IF EXISTS ""FK_SesionesPrivilegiadas_Concesion_Capacidad"";
ALTER TABLE ""ConcesionesPrivilegio"" DROP CONSTRAINT IF EXISTS ""AK_ConcesionesPrivilegio_Id_Capacidad"";
");
            migrationBuilder.DropColumn(
                name: "Capacidad",
                table: "SesionesPrivilegiadas");
        }
    }
}
