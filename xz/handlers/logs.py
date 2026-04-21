import logging
import tempfile
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

from aiogram.filters import Command
from aiogram.types import FSInputFile, Message

from xz.config import get_admin_id


def _build_logs_archive() -> Path:
    logs_dir = Path("logs")
    logs_dir.mkdir(parents=True, exist_ok=True)

    temp_file = tempfile.NamedTemporaryFile(prefix="logs_", suffix=".zip", delete=False)
    temp_path = Path(temp_file.name)
    temp_file.close()

    with ZipFile(temp_path, "w", ZIP_DEFLATED) as archive:
        for file_path in logs_dir.rglob("*"):
            if file_path.is_file():
                archive.write(file_path, arcname=f"logs/{file_path.relative_to(logs_dir).as_posix()}")

    return temp_path


def register_logs_handler(router) -> None:
    @router.message(Command("logs"))
    async def cmd_logs(message: Message):
        user_id = message.from_user.id if message.from_user else None
        chat_type = message.chat.type if message.chat else None
        logging.info("/logs requested by user=%s chat=%s", user_id, chat_type)

        if message.chat.type != "private":
            await message.answer("Доступно только в ЛС", parse_mode="Markdown")
            return

        admin_id = get_admin_id()
        if not message.from_user or message.from_user.id != admin_id:
            await message.answer("Нет доступа :/", parse_mode="Markdown")
            return

        archive_path = _build_logs_archive()
        try:
            await message.answer_document(FSInputFile(archive_path, filename="logs.zip"))
        finally:
            archive_path.unlink(missing_ok=True)
