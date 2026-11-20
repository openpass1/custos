-- 1.2.0: the colour scheme a user picked in the user menu (Users::COLORS; app.css, "Colour schemes"). The default one,
-- «Нейтральный серый», is the base palette of the site.
ALTER TABLE users
    ADD COLUMN color VARCHAR(16) NOT NULL DEFAULT 'neutral' AFTER theme;
