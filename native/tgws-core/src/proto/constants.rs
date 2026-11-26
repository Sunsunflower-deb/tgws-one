pub struct MtProtoConstants;

impl MtProtoConstants {
    pub const HANDSHAKE_LENGTH: usize = 64;
    pub const SKIP_LENGTH: usize = 8;
    pub const PREKEY_LENGTH: usize = 32;
    pub const KEY_LENGTH: usize = 32;
    pub const IV_LENGTH: usize = 16;
    pub const PROTO_TAG_POSITION: usize = 56;
    pub const DC_INDEX_POSITION: usize = 60;

    pub const PROTO_ABRIDGED: u32 = 0xEFEF_EFEF;
    pub const PROTO_INTERMEDIATE: u32 = 0xEEEE_EEEE;
    pub const PROTO_SECURE: u32 = 0xDDDD_DDDD;

    pub const TAG_ABRIDGED: [u8; 4] = [0xEF, 0xEF, 0xEF, 0xEF];
    pub const TAG_INTERMEDIATE: [u8; 4] = [0xEE, 0xEE, 0xEE, 0xEE];
    pub const TAG_SECURE: [u8; 4] = [0xDD, 0xDD, 0xDD, 0xDD];

    pub const RESERVED_FIRST_BYTE: u8 = 0xEF;

    pub fn proto_tag_to_int(tag: &[u8]) -> u32 {
        if tag == Self::TAG_ABRIDGED {
            Self::PROTO_ABRIDGED
        } else if tag == Self::TAG_INTERMEDIATE {
            Self::PROTO_INTERMEDIATE
        } else {
            Self::PROTO_SECURE
        }
    }

    pub fn is_known_tag(tag: &[u8]) -> bool {
        tag == Self::TAG_ABRIDGED || tag == Self::TAG_INTERMEDIATE || tag == Self::TAG_SECURE
    }
}

#[cfg(test)]
mod tests {
    use super::MtProtoConstants;

    #[test]
    fn proto_tag_to_int_matches_csharp() {
        assert_eq!(
            MtProtoConstants::proto_tag_to_int(&[0xEF, 0xEF, 0xEF, 0xEF]),
            0xEFEF_EFEF
        );
        assert_eq!(
            MtProtoConstants::proto_tag_to_int(&[0xEE, 0xEE, 0xEE, 0xEE]),
            0xEEEE_EEEE
        );
        assert_eq!(
            MtProtoConstants::proto_tag_to_int(&[0xDD, 0xDD, 0xDD, 0xDD]),
            0xDDDD_DDDD
        );
        assert_eq!(
            MtProtoConstants::proto_tag_to_int(&[0x00, 0x00, 0x00, 0x00]),
            0xDDDD_DDDD
        );
    }

    #[test]
    fn is_known_tag_matches_csharp() {
        assert!(MtProtoConstants::is_known_tag(&[0xEF, 0xEF, 0xEF, 0xEF]));
        assert!(MtProtoConstants::is_known_tag(&[0xEE, 0xEE, 0xEE, 0xEE]));
        assert!(MtProtoConstants::is_known_tag(&[0xDD, 0xDD, 0xDD, 0xDD]));
        assert!(!MtProtoConstants::is_known_tag(&[0x00, 0x00, 0x00, 0x00]));
    }

    #[test]
    fn constants_values() {
        assert_eq!(MtProtoConstants::HANDSHAKE_LENGTH, 64);
        assert_eq!(MtProtoConstants::SKIP_LENGTH, 8);
        assert_eq!(MtProtoConstants::PREKEY_LENGTH, 32);
        assert_eq!(MtProtoConstants::KEY_LENGTH, 32);
        assert_eq!(MtProtoConstants::IV_LENGTH, 16);
        assert_eq!(MtProtoConstants::PROTO_TAG_POSITION, 56);
        assert_eq!(MtProtoConstants::DC_INDEX_POSITION, 60);
        assert_eq!(MtProtoConstants::PROTO_ABRIDGED, 0xEFEF_EFEF);
        assert_eq!(MtProtoConstants::PROTO_INTERMEDIATE, 0xEEEE_EEEE);
        assert_eq!(MtProtoConstants::PROTO_SECURE, 0xDDDD_DDDD);
    }
}
