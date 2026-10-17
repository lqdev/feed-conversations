using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FeedConversations.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReferenceTarget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TargetUrl",
                table: "References",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TargetUrl",
                table: "References");
        }
    }
}
