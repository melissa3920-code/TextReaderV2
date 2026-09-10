"""Conservative speech-only overrides for Kokoro's English Misaki frontend."""

import re


_WORD = re.compile(r"\b[a-z]+(?:['’][a-z]+)?\b", re.IGNORECASE)
_PROTECTED = re.compile(r"\[[^\[\]]*\]\([^\n)]*\)")
_CLAUSE = re.compile(r"[^.!?;\n,]+")

# Each sense has deliberately specific cues. Conflicting cues leave G2P in charge.
_SENSES = {
    "bass": (
        ("bˈæs", "fish fishing caught catch swam swims lake pond angler largemouth smallmouth"),
        ("bˈAs", "music musical guitar guitarist speakers speaker amplifier frequency frequencies singer singing drum drums clef"),
    ),
    "bow": (
        ("bˈO", "ribbon ribbons package gift tie tied tying arrow arrows archer archery violin cello"),
        ("bˈW", "ship boat vessel yacht waist bowed bowing applause audience respectfully"),
    ),
    "wind": (
        ("wˈɪnd", "blew blows blowing breeze gust gusts windy weather storm trees sails"),
        ("wˈInd", "clock watch crank spool reel coil coils yarn thread winding"),
    ),
    "tear": (
        ("tˈɪɹ", "cry crying cried cheek cheeks wept weeping sob sobbing teardrop"),
        ("tˈɛɹ", "paper fabric cloth rip ripped ripping split seam hole torn shred"),
    ),
    "lead": (
        ("lˈid", "guide group team tour parade expedition troops army leadership"),
        ("lˈɛd", "metal metallic pipe pipes paint poisoning toxic toxicity solder bullet bullets ore"),
    ),
}


def _read_sound(before: list[str], after: list[str]) -> str | None:
    prefix = " ".join(before)
    context = " ".join(before + ["read"] + after)
    # The closest auxiliary wins: "will have read" is perfect, "did read" is not.
    auxiliary = re.search(
        r"\b(have|has|had|haven't|hasn't|hadn't|will|would|can|could|should|must|may|might|do|does|did|don't|doesn't|didn't)"
        r"(?: (?:not|never|already|just|ever|often|always|really))*$", prefix
    )
    if auxiliary:
        return "ɹˈɛd" if auxiliary[1] in {
            "have", "has", "had", "haven't", "hasn't", "hadn't"
        } else "ɹˈid"
    past = bool(re.search(r"\byesterday\b|\blast (?:night|week|month|year)\b|\b\w+ ago\b", context))
    habitual = bool(re.search(r"\bevery (?:day|night|evening|morning|week)\b|\busually\b|\bdaily\b", context))
    if past != habitual:
        return "ɹˈɛd" if past else "ɹˈid"
    return None


def prepare_kokoro_text(text: str, voice: str) -> str:
    """Annotate strong English cues; preserve ambiguous words and user overrides.

    POS heteronyms (record, present, object, conduct) remain with server Misaki.
    This function runs after reader cleanup/chunking and never edits display text.
    """
    if not voice.startswith(("af_", "am_", "bf_", "bm_")):
        return text
    protected = [match.span() for match in _PROTECTED.finditer(text)]
    replacements = []
    for clause in _CLAUSE.finditer(text):
        words = list(_WORD.finditer(clause[0]))
        lowered = [word[0].lower().replace("’", "'") for word in words]
        for index, word in enumerate(words):
            start, end = clause.start() + word.start(), clause.start() + word.end()
            if any(left <= start < right for left, right in protected):
                continue
            name = lowered[index]
            before, after = lowered[max(0, index - 6):index], lowered[index + 1:index + 7]
            sound = None
            if name == "read":
                sound = _read_sound(before, after)
            elif name in _SENSES:
                # Multiple occurrences in a clause need richer parsing; defer to G2P.
                if lowered.count(name) > 1:
                    continue
                clues = set(before + after)
                matches = [phones for phones, cues in _SENSES[name] if clues.intersection(cues.split())]
                if len(matches) == 1:
                    sound = matches[0]
                    if voice.startswith("b") and name == "tear":
                        sound = {"tˈɪɹ": "tˈɪə", "tˈɛɹ": "tˈɛə"}[sound]
            if sound:
                replacements.append((start, end, f"[{word[0]}](/{sound}/)"))
    for start, end, replacement in reversed(replacements):
        text = text[:start] + replacement + text[end:]
    return text
