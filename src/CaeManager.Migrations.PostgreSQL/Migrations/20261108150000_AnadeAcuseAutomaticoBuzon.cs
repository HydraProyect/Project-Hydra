using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeAcuseAutomaticoBuzon : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Con DEFAULT en base: mientras el relevo cambia de ranura, la versión anterior sigue dando de alta buzones
            // sin conocer la columna, y un buzón nace siempre con el acuse desactivado.
            migrationBuilder.AddColumn<bool>(
                name: "AcuseAutomaticoActivo",
                table: "ConexionesIntegracion",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Soporte TALVEG lee esta tabla columna a columna desde SoporteSinColumnasSecretasRestantes: sin este
            // grant, cargar la entidad bajo cae_app_soporte daría 42501. No es un dato secreto.
            migrationBuilder.Sql("""
                GRANT SELECT ("AcuseAutomaticoActivo") ON TABLE public."ConexionesIntegracion" TO cae_app_soporte;
                """);

            // Reserva del tope por remitente: se confirma antes de enviar, así que corta el siguiente acuse aunque el
            // guardado posterior al envío falle. Sin entidad de EF, como ContadoresTicketConversacion: solo la toca la
            // sentencia atómica del repositorio. Guarda una huella y no la dirección, que aquí no hace falta leer.
            // Sin DELETE para el runtime: borrar una reserva reabriría el bucle que corta. Soporte TALVEG no la necesita.
            migrationBuilder.Sql("""
                CREATE TABLE public."ReservasAcuseBuzon" (
                    "TenantId" uuid NOT NULL,
                    "ConexionIntegracionId" uuid NOT NULL,
                    "HuellaRemitente" text NOT NULL,
                    "ReservadaEnUtc" timestamp with time zone NOT NULL,
                    CONSTRAINT "PK_ReservasAcuseBuzon" PRIMARY KEY ("TenantId", "ConexionIntegracionId", "HuellaRemitente"),
                    CONSTRAINT "FK_ReservasAcuseBuzon_ConexionesIntegracion_TenantId_Conexion~"
                        FOREIGN KEY ("TenantId", "ConexionIntegracionId")
                        REFERENCES public."ConexionesIntegracion" ("TenantId", "Id") ON DELETE CASCADE,
                    CONSTRAINT "CK_ReservasAcuseBuzon_HuellaSha256" CHECK (char_length("HuellaRemitente") = 64)
                );

                ALTER TABLE public."ReservasAcuseBuzon" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."ReservasAcuseBuzon" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."ReservasAcuseBuzon"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));

                REVOKE ALL ON TABLE public."ReservasAcuseBuzon" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT ON TABLE public."ReservasAcuseBuzon" TO cae_app_runtime;
                GRANT UPDATE ("ReservadaEnUtc") ON TABLE public."ReservasAcuseBuzon" TO cae_app_runtime;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TABLE public."ReservasAcuseBuzon";""");

            // DROP COLUMN se lleva también el grant de la columna.
            migrationBuilder.DropColumn(
                name: "AcuseAutomaticoActivo",
                table: "ConexionesIntegracion");
        }
    }
}
